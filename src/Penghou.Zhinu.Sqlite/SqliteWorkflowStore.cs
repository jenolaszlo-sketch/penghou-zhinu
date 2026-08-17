using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Penghou.Zhinu.Sqlite;

/// <summary>Implements transactional durable workflow state using SQLite.</summary>
public sealed class SqliteWorkflowStore : IWorkflowStore
{
    private static readonly JsonSerializerOptions SerializerOptions =
        CreateSerializerOptions();
    private readonly ZhinuSqliteOptions options;
    private readonly string connectionString;
    private readonly SemaphoreSlim initializationLock = new(1, 1);
    private volatile bool initialized;

    public SqliteWorkflowStore(ZhinuSqliteOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.DatabasePath);
        if (options.BusyTimeout < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options));
        this.options = options;
        var path = Path.GetFullPath(options.DatabasePath);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        }.ToString();
    }

    public async ValueTask InitializeAsync(
        CancellationToken cancellationToken = default)
    {
        if (initialized)
            return;
        await initializationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (initialized)
                return;
            await using var connection = await OpenAsync(cancellationToken)
                .ConfigureAwait(false);
            if (options.EnableWal)
            {
                await ExecuteAsync(
                    connection,
                    null,
                    "PRAGMA journal_mode = WAL;",
                    cancellationToken).ConfigureAwait(false);
            }
            await ExecuteAsync(
                connection,
                null,
                Schema,
                cancellationToken).ConfigureAwait(false);
            var version = await ScalarAsync<long>(
                connection,
                null,
                "SELECT version FROM zhinu_schema LIMIT 1;",
                cancellationToken).ConfigureAwait(false);
            if (version < CurrentSchemaVersion)
            {
                await MigrateAsync(connection, version, cancellationToken)
                    .ConfigureAwait(false);
            }
            version = await ScalarAsync<long>(
                connection,
                null,
                "SELECT version FROM zhinu_schema LIMIT 1;",
                cancellationToken).ConfigureAwait(false);
            if (version != CurrentSchemaVersion)
            {
                throw new InvalidOperationException(
                    $"Unsupported Zhinu SQLite schema version {version}.");
            }
            await ExecuteAsync(
                connection,
                null,
                "CREATE INDEX IF NOT EXISTS ix_workflow_runs_parent" +
                " ON workflow_runs(parent_run_id);",
                cancellationToken).ConfigureAwait(false);
            initialized = true;
        }
        finally
        {
            initializationLock.Release();
        }
    }

    public async ValueTask CreateRunAsync(
        WorkflowRun run,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        await using var command = CreateCommand(connection, transaction, """
            INSERT INTO workflow_runs
            (id, workflow_name, workflow_version, status, input_json,
             input_type, output_json, output_type, error_json, created_at,
             updated_at, completed_at, deadline, metadata_json, parent_run_id,
             lease_owner, lease_expires_at)
            VALUES
            ($id, $name, $version, $status, $inputJson,
             $inputType, $outputJson, $outputType, $errorJson, $createdAt,
             $updatedAt, $completedAt, $deadline, $metadataJson, $parentRunId,
             $leaseOwner, $leaseExpiresAt);
            """);
        AddRunParameters(command, run);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await InsertEventAsync(
            connection,
            transaction,
            run.Id,
            null,
            WorkflowEventTypes.WorkflowStarted,
            run.CreatedAt,
            null,
            null,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<WorkflowRun?> GetRunAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty)
            throw new ArgumentException("Workflow ID must not be empty.", nameof(id));
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = CreateCommand(connection, null, $"""
            SELECT {RunColumns}
            FROM workflow_runs
            WHERE id = $id;
            """);
        command.Parameters.AddWithValue("$id", Format(id));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadRun(reader)
            : null;
    }

    public async ValueTask<IReadOnlyList<WorkflowRun>> GetRunsAsync(
        RunQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        query.Validate();
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        var where = new List<string>();
        var command = CreateCommand(connection, null, "");
        if (query.Statuses is { Count: > 0 })
        {
            var statusValues = query.Statuses
                .Select(status => ((int)status).ToString(CultureInfo.InvariantCulture))
                .ToArray();
            where.Add($"status IN ({string.Join(", ", statusValues)})");
        }
        if (!string.IsNullOrWhiteSpace(query.WorkflowName))
        {
            where.Add("workflow_name = $name");
            command.Parameters.AddWithValue("$name", query.WorkflowName);
        }
        if (!string.IsNullOrWhiteSpace(query.WorkflowVersion))
        {
            where.Add("workflow_version = $version");
            command.Parameters.AddWithValue("$version", query.WorkflowVersion);
        }
        if (query.CreatedAfter is not null)
        {
            where.Add("created_at >= $createdAfter");
            command.Parameters.AddWithValue(
                "$createdAfter",
                FormatTimestamp(query.CreatedAfter.Value));
        }
        if (query.CreatedBefore is not null)
        {
            where.Add("created_at <= $createdBefore");
            command.Parameters.AddWithValue(
                "$createdBefore",
                FormatTimestamp(query.CreatedBefore.Value));
        }
        if (query.AfterId is { } afterId)
        {
            var afterCreated = await GetCreatedAtAsync(
                connection,
                afterId,
                cancellationToken).ConfigureAwait(false);
            where.Add("(created_at, id) > ($afterCreated, $afterId)");
            command.Parameters.AddWithValue("$afterCreated", afterCreated);
            command.Parameters.AddWithValue("$afterId", Format(afterId));
        }
        var whereClause = where.Count == 0 ? string.Empty : $"WHERE {string.Join(" AND ", where)}";
        command.CommandText = $"""
            SELECT {RunColumns}
            FROM workflow_runs
            {whereClause}
            ORDER BY created_at, id
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", query.Limit);
        var results = new List<WorkflowRun>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            results.Add(ReadRun(reader));
        return results;
    }

    public async ValueTask<WorkflowRun?> UpdateRunMetadataAsync(
        Guid workflowRunId,
        string? metadataJson,
        CancellationToken cancellationToken = default)
    {
        if (workflowRunId == Guid.Empty)
            throw new ArgumentException("Workflow ID must not be empty.", nameof(workflowRunId));
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        await using var command = CreateCommand(connection, transaction, """
            UPDATE workflow_runs
            SET metadata_json = $metadataJson, updated_at = $now
            WHERE id = $id;
            """);
        command.Parameters.AddWithValue("$metadataJson", DbValue(metadataJson));
        command.Parameters.AddWithValue("$now", FormatTimestamp(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("$id", Format(workflowRunId));
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }
        await using var readCommand = CreateCommand(connection, transaction, $"""
            SELECT {RunColumns}
            FROM workflow_runs
            WHERE id = $id;
            """);
        readCommand.Parameters.AddWithValue("$id", Format(workflowRunId));
        await using var reader = await readCommand.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        var run = await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadRun(reader)
            : null;
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return run;
    }

    public async ValueTask<IReadOnlyList<WorkflowStepRun>> GetStepsAsync(
        Guid workflowRunId,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = CreateCommand(connection, null, $"""
            SELECT {StepColumns}
            FROM workflow_steps
            WHERE workflow_run_id = $runId
            ORDER BY created_at, step_key;
            """);
        command.Parameters.AddWithValue("$runId", Format(workflowRunId));
        return await ReadStepsAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<WorkflowEvent>> GetEventsAsync(
        Guid workflowRunId,
        long afterSequence,
        int limit,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = CreateCommand(connection, null, """
            SELECT sequence, workflow_run_id, step_key, event_type,
                   timestamp, attempt, data_json
            FROM workflow_events
            WHERE workflow_run_id = $runId AND sequence > $after
            ORDER BY sequence
            LIMIT $limit;
            """);
        command.Parameters.AddWithValue("$runId", Format(workflowRunId));
        command.Parameters.AddWithValue("$after", afterSequence);
        command.Parameters.AddWithValue("$limit", limit);
        var results = new List<WorkflowEvent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new WorkflowEvent
            {
                Sequence = reader.GetInt64(0),
                WorkflowRunId = Guid.Parse(reader.GetString(1)),
                StepKey = GetNullableString(reader, 2),
                EventType = reader.GetString(3),
                Timestamp = ParseTimestamp(reader.GetString(4)),
                Attempt = reader.IsDBNull(5) ? null : reader.GetInt32(5),
                DataJson = GetNullableString(reader, 6)
            });
        }
        return results;
    }

    public async ValueTask<WorkflowEvent> AppendEventAsync(
        Guid workflowRunId,
        string eventType,
        string? dataJson,
        string? stepKey = null,
        int? attempt = null,
        CancellationToken cancellationToken = default)
    {
        if (workflowRunId == Guid.Empty)
            throw new ArgumentException("Workflow ID must not be empty.", nameof(workflowRunId));
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        var timestamp = DateTimeOffset.UtcNow;
        await using var command = CreateCommand(connection, null, """
            INSERT INTO workflow_events
            (workflow_run_id, step_key, event_type, timestamp, attempt, data_json)
            VALUES ($runId, $stepKey, $eventType, $timestamp, $attempt, $dataJson);
            SELECT last_insert_rowid();
            """);
        command.Parameters.AddWithValue("$runId", Format(workflowRunId));
        command.Parameters.AddWithValue("$stepKey", DbValue(stepKey));
        command.Parameters.AddWithValue("$eventType", eventType);
        command.Parameters.AddWithValue("$timestamp", FormatTimestamp(timestamp));
        command.Parameters.AddWithValue("$attempt", attempt is null ? DBNull.Value : attempt.Value);
        command.Parameters.AddWithValue("$dataJson", DbValue(dataJson));
        var sequence = Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
        return new WorkflowEvent
        {
            Sequence = sequence,
            WorkflowRunId = workflowRunId,
            StepKey = stepKey,
            EventType = eventType,
            Timestamp = timestamp,
            Attempt = attempt,
            DataJson = dataJson
        };
    }

    public async ValueTask<IReadOnlyList<Guid>> GetRunnableRunIdsAsync(
        DateTimeOffset now,
        int limit,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = CreateCommand(connection, null, """
            SELECT id
            FROM workflow_runs
            WHERE status IN ($pending, $running)
              AND (lease_expires_at IS NULL OR lease_expires_at <= $now)
            ORDER BY created_at, id
            LIMIT $limit;
            """);
        command.Parameters.AddWithValue("$pending", (int)WorkflowStatus.Pending);
        command.Parameters.AddWithValue("$running", (int)WorkflowStatus.Running);
        command.Parameters.AddWithValue("$now", FormatTimestamp(now));
        command.Parameters.AddWithValue("$limit", limit);
        var results = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            results.Add(Guid.Parse(reader.GetString(0)));
        return results;
    }

    public async ValueTask<bool> TryClaimRunAsync(
        Guid workflowRunId,
        string ownerId,
        DateTimeOffset now,
        DateTimeOffset leaseExpiresAt,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var status = await ReadRunStatusAsync(
            connection,
            transaction,
            workflowRunId,
            cancellationToken).ConfigureAwait(false);
        if (status is not (WorkflowStatus.Pending or WorkflowStatus.Running))
            return false;
        await using var command = CreateCommand(connection, transaction, """
            UPDATE workflow_runs
            SET status = $running,
                updated_at = $now,
                lease_owner = $owner,
                lease_expires_at = $expires
            WHERE id = $id
              AND status IN ($pending, $running)
              AND (lease_expires_at IS NULL OR lease_expires_at <= $now);
            """);
        command.Parameters.AddWithValue("$running", (int)WorkflowStatus.Running);
        command.Parameters.AddWithValue("$pending", (int)WorkflowStatus.Pending);
        command.Parameters.AddWithValue("$now", FormatTimestamp(now));
        command.Parameters.AddWithValue("$owner", ownerId);
        command.Parameters.AddWithValue("$expires", FormatTimestamp(leaseExpiresAt));
        command.Parameters.AddWithValue("$id", Format(workflowRunId));
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            return false;
        if (status == WorkflowStatus.Running)
        {
            await InsertEventAsync(
                connection,
                transaction,
                workflowRunId,
                null,
                WorkflowEventTypes.WorkflowResumed,
                now,
                null,
                null,
                cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public ValueTask<bool> RenewRunLeaseAsync(
        Guid workflowRunId,
        string ownerId,
        DateTimeOffset leaseExpiresAt,
        CancellationToken cancellationToken = default) =>
        UpdateLeaseAsync(
            "workflow_runs",
            workflowRunId,
            ownerId,
            leaseExpiresAt,
            WorkflowStatus.Running,
            cancellationToken);

    public async ValueTask ReleaseRunLeaseAsync(
        Guid workflowRunId,
        string ownerId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = CreateCommand(connection, null, """
            UPDATE workflow_runs
            SET lease_owner = NULL, lease_expires_at = NULL, updated_at = $now
            WHERE id = $id AND lease_owner = $owner AND status = $running;
            """);
        command.Parameters.AddWithValue("$now", FormatTimestamp(now));
        command.Parameters.AddWithValue("$id", Format(workflowRunId));
        command.Parameters.AddWithValue("$owner", ownerId);
        command.Parameters.AddWithValue("$running", (int)WorkflowStatus.Running);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask CompleteRunAsync(
        Guid workflowRunId,
        string ownerId,
        string? outputJson,
        string outputType,
        DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        FinishRunAsync(
            workflowRunId,
            ownerId,
            WorkflowStatus.Completed,
            outputJson,
            outputType,
            null,
            WorkflowEventTypes.WorkflowCompleted,
            now,
            cancellationToken);

    public ValueTask FailRunAsync(
        Guid workflowRunId,
        string ownerId,
        WorkflowError error,
        DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        FinishRunAsync(
            workflowRunId,
            ownerId,
            WorkflowStatus.Failed,
            null,
            string.Empty,
            error,
            WorkflowEventTypes.WorkflowFailed,
            now,
            cancellationToken);

    public async ValueTask CancelRunAsync(
        Guid workflowRunId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        await using var runCommand = CreateCommand(connection, transaction, """
            UPDATE workflow_runs
            SET status = $cancelled, updated_at = $now, completed_at = $now,
                lease_owner = NULL, lease_expires_at = NULL
            WHERE id = $id AND status NOT IN ($completed, $failed, $cancelled);
            """);
        runCommand.Parameters.AddWithValue("$cancelled", (int)WorkflowStatus.Cancelled);
        runCommand.Parameters.AddWithValue("$completed", (int)WorkflowStatus.Completed);
        runCommand.Parameters.AddWithValue("$failed", (int)WorkflowStatus.Failed);
        runCommand.Parameters.AddWithValue("$now", FormatTimestamp(now));
        runCommand.Parameters.AddWithValue("$id", Format(workflowRunId));
        if (await runCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
            return;
        await using var stepCommand = CreateCommand(connection, transaction, """
            UPDATE workflow_steps
            SET status = $cancelled, completed_at = $now,
                lease_owner = NULL, lease_expires_at = NULL
            WHERE workflow_run_id = $id
              AND status IN ($pending, $running, $waiting);
            """);
        stepCommand.Parameters.AddWithValue("$cancelled", (int)StepStatus.Cancelled);
        stepCommand.Parameters.AddWithValue("$pending", (int)StepStatus.Pending);
        stepCommand.Parameters.AddWithValue("$running", (int)StepStatus.Running);
        stepCommand.Parameters.AddWithValue("$waiting", (int)StepStatus.Waiting);
        stepCommand.Parameters.AddWithValue("$now", FormatTimestamp(now));
        stepCommand.Parameters.AddWithValue("$id", Format(workflowRunId));
        await stepCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await InsertEventAsync(
            connection,
            transaction,
            workflowRunId,
            null,
            WorkflowEventTypes.WorkflowCancelled,
            now,
            null,
            null,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<StepClaimResult> ClaimStepAsync(
        StepClaimRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateClaim(request);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var runStatus = await ReadRunStatusAsync(
            connection,
            transaction,
            request.WorkflowRunId,
            cancellationToken).ConfigureAwait(false);
        if (runStatus == WorkflowStatus.Cancelled)
        {
            return new StepClaimResult(
                StepClaimDisposition.Cancelled,
                CancelledPlaceholder(request));
        }
        if (runStatus is not (WorkflowStatus.Pending or WorkflowStatus.Running))
        {
            throw new WorkflowStateException(
                $"Workflow '{request.WorkflowRunId:D}' is not executable in state '{runStatus}'.");
        }
        var existing = await ReadStepAsync(
            connection,
            transaction,
            request.WorkflowRunId,
            request.StepKey,
            cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            var created = new WorkflowStepRun
            {
                Id = Guid.NewGuid(),
                WorkflowRunId = request.WorkflowRunId,
                StepKey = request.StepKey,
                Status = StepStatus.Running,
                Attempt = 1,
                CreatedAt = request.Now,
                StartedAt = request.Now,
                InputJson = request.InputJson,
                InputType = request.InputType,
                InputHash = request.InputHash,
                OutputType = request.OutputType,
                LeaseOwner = request.OwnerId,
                LeaseExpiresAt = request.LeaseExpiresAt
            };
            await InsertStepAsync(
                connection,
                transaction,
                created,
                cancellationToken).ConfigureAwait(false);
            await InsertEventAsync(
                connection,
                transaction,
                request.WorkflowRunId,
                request.StepKey,
                WorkflowEventTypes.StepStarted,
                request.Now,
                1,
                null,
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new StepClaimResult(StepClaimDisposition.Acquired, created);
        }

        ValidateStepContract(existing, request);
        if (existing.Status == StepStatus.Completed)
        {
            await InsertEventAsync(
                connection,
                transaction,
                request.WorkflowRunId,
                request.StepKey,
                WorkflowEventTypes.StepReused,
                request.Now,
                existing.Attempt,
                null,
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new StepClaimResult(StepClaimDisposition.Reused, existing);
        }
        if (existing.Status == StepStatus.Failed)
            return new StepClaimResult(StepClaimDisposition.Failed, existing);
        if (existing.Status == StepStatus.Cancelled)
            return new StepClaimResult(StepClaimDisposition.Cancelled, existing);
        if (existing.Status == StepStatus.Waiting &&
            (existing.SignalName is not null ||
             (existing.AvailableAt is not null &&
              existing.AvailableAt > request.Now)))
        {
            return new StepClaimResult(StepClaimDisposition.Waiting, existing);
        }
        if (existing.Status == StepStatus.Running &&
            existing.LeaseExpiresAt > request.Now)
        {
            return new StepClaimResult(StepClaimDisposition.Busy, existing);
        }

        var attempt = existing.Attempt + 1;
        await using var claimCommand = CreateCommand(connection, transaction, """
            UPDATE workflow_steps
            SET status = $running, attempt = $attempt, started_at = $now,
                available_at = NULL, error_json = NULL,
                lease_owner = $owner, lease_expires_at = $expires
            WHERE id = $id;
            """);
        claimCommand.Parameters.AddWithValue("$running", (int)StepStatus.Running);
        claimCommand.Parameters.AddWithValue("$attempt", attempt);
        claimCommand.Parameters.AddWithValue("$now", FormatTimestamp(request.Now));
        claimCommand.Parameters.AddWithValue("$owner", request.OwnerId);
        claimCommand.Parameters.AddWithValue("$expires", FormatTimestamp(request.LeaseExpiresAt));
        claimCommand.Parameters.AddWithValue("$id", Format(existing.Id));
        await claimCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await InsertEventAsync(
            connection,
            transaction,
            request.WorkflowRunId,
            request.StepKey,
            WorkflowEventTypes.StepStarted,
            request.Now,
            attempt,
            null,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new StepClaimResult(
            StepClaimDisposition.Acquired,
            existing with
            {
                Status = StepStatus.Running,
                Attempt = attempt,
                StartedAt = request.Now,
                AvailableAt = null,
                Error = null,
                LeaseOwner = request.OwnerId,
                LeaseExpiresAt = request.LeaseExpiresAt
            });
    }

    public ValueTask<bool> RenewStepLeaseAsync(
        Guid stepId,
        string ownerId,
        DateTimeOffset leaseExpiresAt,
        CancellationToken cancellationToken = default) =>
        UpdateLeaseAsync(
            "workflow_steps",
            stepId,
            ownerId,
            leaseExpiresAt,
            StepStatus.Running,
            cancellationToken);

    public ValueTask CompleteStepAsync(
        Guid stepId,
        string ownerId,
        string? outputJson,
        DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        FinishStepAsync(
            stepId,
            ownerId,
            StepStatus.Completed,
            outputJson,
            null,
            null,
            WorkflowEventTypes.StepCompleted,
            now,
            cancellationToken);

    public async ValueTask FailStepAsync(
        Guid stepId,
        string ownerId,
        WorkflowError error,
        DateTimeOffset? retryAt,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var step = await ReadStepByIdAsync(
            connection,
            transaction,
            stepId,
            cancellationToken).ConfigureAwait(false) ??
            throw new KeyNotFoundException($"Step '{stepId:D}' does not exist.");
        var status = retryAt is null ? StepStatus.Failed : StepStatus.Waiting;
        await using var command = CreateCommand(connection, transaction, """
            UPDATE workflow_steps
            SET status = $status, error_json = $errorJson,
                available_at = $availableAt, completed_at = $completedAt,
                lease_owner = NULL, lease_expires_at = NULL
            WHERE id = $id AND status = $running AND lease_owner = $owner;
            """);
        command.Parameters.AddWithValue("$status", (int)status);
        command.Parameters.AddWithValue("$errorJson", SerializeError(error)!);
        command.Parameters.AddWithValue(
            "$availableAt",
            DbValue(FormatNullable(retryAt)));
        command.Parameters.AddWithValue(
            "$completedAt",
            retryAt is null ? FormatTimestamp(now) : DBNull.Value);
        command.Parameters.AddWithValue("$id", Format(stepId));
        command.Parameters.AddWithValue("$running", (int)StepStatus.Running);
        command.Parameters.AddWithValue("$owner", ownerId);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new WorkflowStateException("Step failure requires an owned running lease.");
        await InsertEventAsync(
            connection,
            transaction,
            step.WorkflowRunId,
            step.StepKey,
            WorkflowEventTypes.StepFailed,
            now,
            step.Attempt,
            JsonSerializer.Serialize(error, SerializerOptions),
            cancellationToken).ConfigureAwait(false);
        if (retryAt is not null)
        {
            await InsertEventAsync(
                connection,
                transaction,
                step.WorkflowRunId,
                step.StepKey,
                WorkflowEventTypes.RetryScheduled,
                now,
                step.Attempt,
                JsonSerializer.Serialize(
                    new { availableAt = retryAt },
                    SerializerOptions),
                cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask ScheduleDelayAsync(
        Guid stepId,
        string ownerId,
        DateTimeOffset availableAt,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await FinishStepAsync(
            stepId,
            ownerId,
            StepStatus.Waiting,
            null,
            null,
            availableAt,
            WorkflowEventTypes.DelayScheduled,
            now,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask CompleteDelayAsync(
        Guid stepId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var step = await ReadStepByIdAsync(
            connection,
            transaction,
            stepId,
            cancellationToken).ConfigureAwait(false) ??
            throw new KeyNotFoundException($"Step '{stepId:D}' does not exist.");
        if (step.Status == StepStatus.Completed)
            return;
        if (step.Status != StepStatus.Waiting || step.AvailableAt > now)
            throw new WorkflowStateException("Durable delay is not eligible to complete.");
        await using var command = CreateCommand(connection, transaction, """
            UPDATE workflow_steps
            SET status = $completed, completed_at = $now,
                lease_owner = NULL, lease_expires_at = NULL
            WHERE id = $id AND status = $waiting AND available_at <= $now;
            """);
        command.Parameters.AddWithValue("$completed", (int)StepStatus.Completed);
        command.Parameters.AddWithValue("$waiting", (int)StepStatus.Waiting);
        command.Parameters.AddWithValue("$now", FormatTimestamp(now));
        command.Parameters.AddWithValue("$id", Format(stepId));
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new WorkflowStateException("Durable delay completion lost its state transition.");
        await InsertEventAsync(
            connection,
            transaction,
            step.WorkflowRunId,
            step.StepKey,
            WorkflowEventTypes.StepCompleted,
            now,
            step.Attempt,
            null,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<int> RecoverExpiredLeasesAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        await using var select = CreateCommand(connection, transaction, $"""
            SELECT {StepColumns}
            FROM workflow_steps
            WHERE status = $running AND lease_expires_at <= $now;
            """);
        select.Parameters.AddWithValue("$running", (int)StepStatus.Running);
        select.Parameters.AddWithValue("$now", FormatTimestamp(now));
        var expired = await ReadStepsAsync(select, cancellationToken)
            .ConfigureAwait(false);
        await using var steps = CreateCommand(connection, transaction, """
            UPDATE workflow_steps
            SET status = $waiting, available_at = $now,
                lease_owner = NULL, lease_expires_at = NULL
            WHERE status = $running AND lease_expires_at <= $now;
            """);
        steps.Parameters.AddWithValue("$waiting", (int)StepStatus.Waiting);
        steps.Parameters.AddWithValue("$running", (int)StepStatus.Running);
        steps.Parameters.AddWithValue("$now", FormatTimestamp(now));
        await steps.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await using var runs = CreateCommand(connection, transaction, """
            UPDATE workflow_runs
            SET lease_owner = NULL, lease_expires_at = NULL, updated_at = $now
            WHERE status = $running AND lease_expires_at <= $now;
            """);
        runs.Parameters.AddWithValue("$running", (int)WorkflowStatus.Running);
        runs.Parameters.AddWithValue("$now", FormatTimestamp(now));
        await runs.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        foreach (var step in expired)
        {
            await InsertEventAsync(
                connection,
                transaction,
                step.WorkflowRunId,
                step.StepKey,
                WorkflowEventTypes.LeaseRecovered,
                now,
                step.Attempt,
                null,
                cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return expired.Count;
    }

    public async ValueTask<int> PurgeRunsAsync(
        DateTimeOffset olderThan,
        IReadOnlyList<WorkflowStatus>? statuses = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var statusClause = string.Empty;
        if (statuses is { Count: > 0 })
        {
            var values = string.Join(
                ", ",
                statuses.Select(status =>
                    ((int)status).ToString(CultureInfo.InvariantCulture)));
            statusClause = $" AND status IN ({values})";
        }
        await using var command = CreateCommand(connection, transaction, $"""
            DELETE FROM workflow_runs
            WHERE created_at < $olderThan{statusClause};
            """);
        command.Parameters.AddWithValue("$olderThan", FormatTimestamp(olderThan));
        var deleted = await command.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return deleted;
    }

    public async ValueTask<int> RestartStepAsync(
        Guid workflowRunId,
        string stepKey,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        if (workflowRunId == Guid.Empty)
            throw new ArgumentException("Workflow ID must not be empty.", nameof(workflowRunId));
        ArgumentException.ThrowIfNullOrWhiteSpace(stepKey);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var runStatus = await ReadRunStatusAsync(
            connection,
            transaction,
            workflowRunId,
            cancellationToken).ConfigureAwait(false);
        if (runStatus is null)
        {
            throw new KeyNotFoundException(
                $"Workflow '{workflowRunId:D}' does not exist.");
        }
        var target = await ReadStepAsync(
            connection,
            transaction,
            workflowRunId,
            stepKey,
            cancellationToken).ConfigureAwait(false) ??
            throw new KeyNotFoundException(
                $"Step '{stepKey}' does not exist in workflow '{workflowRunId:D}'.");
        await using var resetRun = CreateCommand(connection, transaction, """
            UPDATE workflow_runs
            SET status = $pending, output_json = NULL,
                error_json = NULL, completed_at = NULL,
                lease_owner = NULL, lease_expires_at = NULL, updated_at = $now
            WHERE id = $id;
            """);
        resetRun.Parameters.AddWithValue("$pending", (int)WorkflowStatus.Pending);
        resetRun.Parameters.AddWithValue("$now", FormatTimestamp(now));
        resetRun.Parameters.AddWithValue("$id", Format(workflowRunId));
        await resetRun.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await using var delete = CreateCommand(connection, transaction, """
            DELETE FROM workflow_steps
            WHERE workflow_run_id = $runId AND created_at >= $targetCreatedAt;
            """);
        delete.Parameters.AddWithValue("$runId", Format(workflowRunId));
        delete.Parameters.AddWithValue(
            "$targetCreatedAt",
            FormatTimestamp(target.CreatedAt));
        var invalidated = await delete.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false);
        await InsertEventAsync(
            connection,
            transaction,
            workflowRunId,
            stepKey,
            WorkflowEventTypes.StepRestarted,
            now,
            null,
            JsonSerializer.Serialize(
                new { stepKey, invalidatedSteps = invalidated },
                SerializerOptions),
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return invalidated;
    }

    public async ValueTask SendSignalAsync(
        Guid workflowRunId,
        string signalName,
        string? dataJson,
        CancellationToken cancellationToken = default)
    {
        if (workflowRunId == Guid.Empty)
            throw new ArgumentException("Workflow ID must not be empty.", nameof(workflowRunId));
        ArgumentException.ThrowIfNullOrWhiteSpace(signalName);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var runStatus = await ReadRunStatusAsync(
            connection,
            transaction,
            workflowRunId,
            cancellationToken).ConfigureAwait(false);
        if (runStatus is null)
        {
            throw new KeyNotFoundException(
                $"Workflow '{workflowRunId:D}' does not exist.");
        }
        var now = DateTimeOffset.UtcNow;
        await using var command = CreateCommand(connection, transaction, """
            INSERT INTO workflow_signals
            (id, workflow_run_id, signal_name, data_json, created_at)
            VALUES ($id, $runId, $name, $dataJson, $now);
            """);
        command.Parameters.AddWithValue("$id", Format(Guid.NewGuid()));
        command.Parameters.AddWithValue("$runId", Format(workflowRunId));
        command.Parameters.AddWithValue("$name", signalName);
        command.Parameters.AddWithValue("$dataJson", DbValue(dataJson));
        command.Parameters.AddWithValue("$now", FormatTimestamp(now));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await InsertEventAsync(
            connection,
            transaction,
            workflowRunId,
            null,
            WorkflowEventTypes.SignalSent,
            now,
            null,
            JsonSerializer.Serialize(
                new { signalName, data = dataJson },
                SerializerOptions),
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<SignalDelivery?> TryDeliverSignalAsync(
        Guid stepId,
        string ownerId,
        string signalName,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(signalName);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var step = await ReadStepByIdAsync(
            connection,
            transaction,
            stepId,
            cancellationToken).ConfigureAwait(false) ??
            throw new KeyNotFoundException($"Step '{stepId:D}' does not exist.");
        if (step.Status == StepStatus.Completed)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new SignalDelivery(step.OutputJson);
        }
        if (step.SignalName is not null &&
            !string.Equals(step.SignalName, signalName, StringComparison.Ordinal))
        {
            throw new WorkflowStateException(
                $"Step '{step.StepKey}' is waiting on signal '{step.SignalName}', not '{signalName}'.");
        }
        if (step.Status is not (StepStatus.Waiting or StepStatus.Running))
            return null;
        var effectiveName = step.SignalName ?? signalName;
        await using var select = CreateCommand(connection, transaction, """
            SELECT id, data_json
            FROM workflow_signals
            WHERE workflow_run_id = $runId
              AND signal_name = $name
              AND delivered_step_id IS NULL
            ORDER BY created_at
            LIMIT 1;
            """);
        select.Parameters.AddWithValue("$runId", Format(step.WorkflowRunId));
        select.Parameters.AddWithValue("$name", effectiveName);
        string? signalId = null;
        string? dataJson = null;
        await using (var reader = await select.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                signalId = reader.GetString(0);
                dataJson = GetNullableString(reader, 1);
            }
        }
        if (signalId is null)
        {
            if (step.Status == StepStatus.Running)
            {
                await using var waiting = CreateCommand(connection, transaction, """
                    UPDATE workflow_steps
                    SET status = $waiting, signal_name = $name,
                        available_at = NULL, error_json = NULL,
                        lease_owner = NULL, lease_expires_at = NULL
                    WHERE id = $id AND status = $running;
                    """);
                waiting.Parameters.AddWithValue("$waiting", (int)StepStatus.Waiting);
                waiting.Parameters.AddWithValue("$running", (int)StepStatus.Running);
                waiting.Parameters.AddWithValue("$name", effectiveName);
                waiting.Parameters.AddWithValue("$id", Format(stepId));
                await waiting.ExecuteNonQueryAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        await using var claim = CreateCommand(connection, transaction, """
            UPDATE workflow_signals
            SET delivered_step_id = $stepId
            WHERE id = $id AND delivered_step_id IS NULL;
            """);
        claim.Parameters.AddWithValue("$stepId", Format(stepId));
        claim.Parameters.AddWithValue("$id", signalId);
        if (await claim.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }
        await using var complete = CreateCommand(connection, transaction, """
            UPDATE workflow_steps
            SET status = $completed, output_json = $outputJson,
                signal_name = $name, completed_at = $now,
                available_at = NULL, error_json = NULL,
                lease_owner = NULL, lease_expires_at = NULL
            WHERE id = $id;
            """);
        complete.Parameters.AddWithValue("$completed", (int)StepStatus.Completed);
        complete.Parameters.AddWithValue("$outputJson", DbValue(dataJson));
        complete.Parameters.AddWithValue("$name", effectiveName);
        complete.Parameters.AddWithValue("$now", FormatTimestamp(now));
        complete.Parameters.AddWithValue("$id", Format(stepId));
        await complete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await InsertEventAsync(
            connection,
            transaction,
            step.WorkflowRunId,
            step.StepKey,
            WorkflowEventTypes.SignalDelivered,
            now,
            step.Attempt,
            JsonSerializer.Serialize(
                new { signalName = effectiveName, data = dataJson },
                SerializerOptions),
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new SignalDelivery(dataJson);
    }

    private async ValueTask FinishRunAsync(
        Guid workflowRunId,
        string ownerId,
        WorkflowStatus status,
        string? outputJson,
        string outputType,
        WorkflowError? error,
        string eventType,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        await using var command = CreateCommand(connection, transaction, """
            UPDATE workflow_runs
            SET status = $status, output_json = $outputJson,
                output_type = CASE WHEN $outputType = '' THEN output_type ELSE $outputType END,
                error_json = $errorJson, updated_at = $now, completed_at = $now,
                lease_owner = NULL, lease_expires_at = NULL
            WHERE id = $id AND status = $running AND lease_owner = $owner;
            """);
        command.Parameters.AddWithValue("$status", (int)status);
        command.Parameters.AddWithValue("$outputJson", DbValue(outputJson));
        command.Parameters.AddWithValue("$outputType", outputType);
        command.Parameters.AddWithValue("$errorJson", DbValue(SerializeError(error)));
        command.Parameters.AddWithValue("$now", FormatTimestamp(now));
        command.Parameters.AddWithValue("$id", Format(workflowRunId));
        command.Parameters.AddWithValue("$running", (int)WorkflowStatus.Running);
        command.Parameters.AddWithValue("$owner", ownerId);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new WorkflowStateException("Workflow completion requires an owned running lease.");
        await InsertEventAsync(
            connection,
            transaction,
            workflowRunId,
            null,
            eventType,
            now,
            null,
            error is null ? null : JsonSerializer.Serialize(error, SerializerOptions),
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask FinishStepAsync(
        Guid stepId,
        string ownerId,
        StepStatus status,
        string? outputJson,
        WorkflowError? error,
        DateTimeOffset? availableAt,
        string eventType,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var step = await ReadStepByIdAsync(
            connection,
            transaction,
            stepId,
            cancellationToken).ConfigureAwait(false) ??
            throw new KeyNotFoundException($"Step '{stepId:D}' does not exist.");
        await using var command = CreateCommand(connection, transaction, """
            UPDATE workflow_steps
            SET status = $status, output_json = $outputJson,
                error_json = $errorJson, available_at = $availableAt,
                completed_at = $completedAt,
                lease_owner = NULL, lease_expires_at = NULL
            WHERE id = $id AND status = $running AND lease_owner = $owner;
            """);
        command.Parameters.AddWithValue("$status", (int)status);
        command.Parameters.AddWithValue("$outputJson", DbValue(outputJson));
        command.Parameters.AddWithValue("$errorJson", DbValue(SerializeError(error)));
        command.Parameters.AddWithValue("$availableAt", DbValue(FormatNullable(availableAt)));
        command.Parameters.AddWithValue(
            "$completedAt",
            status is StepStatus.Completed or StepStatus.Failed
                ? FormatTimestamp(now)
                : DBNull.Value);
        command.Parameters.AddWithValue("$id", Format(stepId));
        command.Parameters.AddWithValue("$running", (int)StepStatus.Running);
        command.Parameters.AddWithValue("$owner", ownerId);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new WorkflowStateException("Step transition requires an owned running lease.");
        await InsertEventAsync(
            connection,
            transaction,
            step.WorkflowRunId,
            step.StepKey,
            eventType,
            now,
            step.Attempt,
            error is null ? null : JsonSerializer.Serialize(error, SerializerOptions),
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<bool> UpdateLeaseAsync<TStatus>(
        string table,
        Guid id,
        string ownerId,
        DateTimeOffset leaseExpiresAt,
        TStatus runningStatus,
        CancellationToken cancellationToken)
        where TStatus : struct, Enum
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = CreateCommand(connection, null, $"""
            UPDATE {table}
            SET lease_expires_at = $expires
            WHERE id = $id AND lease_owner = $owner AND status = $running;
            """);
        command.Parameters.AddWithValue("$expires", FormatTimestamp(leaseExpiresAt));
        command.Parameters.AddWithValue("$id", Format(id));
        command.Parameters.AddWithValue("$owner", ownerId);
        command.Parameters.AddWithValue("$running", Convert.ToInt32(runningStatus, CultureInfo.InvariantCulture));
        return await command.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false) == 1;
    }

    private static async ValueTask InsertStepAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        WorkflowStepRun step,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction, """
            INSERT INTO workflow_steps
            (id, workflow_run_id, step_key, status, attempt, input_json,
             input_type, input_hash, output_json, output_type, error_json,
             signal_name, created_at, started_at, completed_at, available_at,
             lease_owner, lease_expires_at)
            VALUES
            ($id, $runId, $stepKey, $status, $attempt, $inputJson,
             $inputType, $inputHash, $outputJson, $outputType, $errorJson,
             $signalName, $createdAt, $startedAt, $completedAt, $availableAt,
             $leaseOwner, $leaseExpiresAt);
            """);
        command.Parameters.AddWithValue("$id", Format(step.Id));
        command.Parameters.AddWithValue("$runId", Format(step.WorkflowRunId));
        command.Parameters.AddWithValue("$stepKey", step.StepKey);
        command.Parameters.AddWithValue("$status", (int)step.Status);
        command.Parameters.AddWithValue("$attempt", step.Attempt);
        command.Parameters.AddWithValue("$inputJson", DbValue(step.InputJson));
        command.Parameters.AddWithValue("$inputType", DbValue(step.InputType));
        command.Parameters.AddWithValue("$inputHash", DbValue(step.InputHash));
        command.Parameters.AddWithValue("$outputJson", DbValue(step.OutputJson));
        command.Parameters.AddWithValue("$outputType", DbValue(step.OutputType));
        command.Parameters.AddWithValue("$errorJson", DbValue(SerializeError(step.Error)));
        command.Parameters.AddWithValue("$signalName", DbValue(step.SignalName));
        command.Parameters.AddWithValue("$createdAt", FormatTimestamp(step.CreatedAt));
        command.Parameters.AddWithValue("$startedAt", DbValue(FormatNullable(step.StartedAt)));
        command.Parameters.AddWithValue("$completedAt", DbValue(FormatNullable(step.CompletedAt)));
        command.Parameters.AddWithValue("$availableAt", DbValue(FormatNullable(step.AvailableAt)));
        command.Parameters.AddWithValue("$leaseOwner", DbValue(step.LeaseOwner));
        command.Parameters.AddWithValue("$leaseExpiresAt", DbValue(FormatNullable(step.LeaseExpiresAt)));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask InsertEventAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid workflowRunId,
        string? stepKey,
        string eventType,
        DateTimeOffset timestamp,
        int? attempt,
        string? dataJson,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction, """
            INSERT INTO workflow_events
            (workflow_run_id, step_key, event_type, timestamp, attempt, data_json)
            VALUES ($runId, $stepKey, $eventType, $timestamp, $attempt, $dataJson);
            """);
        command.Parameters.AddWithValue("$runId", Format(workflowRunId));
        command.Parameters.AddWithValue("$stepKey", DbValue(stepKey));
        command.Parameters.AddWithValue("$eventType", eventType);
        command.Parameters.AddWithValue("$timestamp", FormatTimestamp(timestamp));
        command.Parameters.AddWithValue("$attempt", attempt is null ? DBNull.Value : attempt.Value);
        command.Parameters.AddWithValue("$dataJson", DbValue(dataJson));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<WorkflowStatus?> ReadRunStatusAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid workflowRunId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction, """
            SELECT status FROM workflow_runs WHERE id = $id;
            """);
        command.Parameters.AddWithValue("$id", Format(workflowRunId));
        var value = await command.ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false);
        return value is null ? null : (WorkflowStatus)Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    private static async ValueTask<WorkflowStepRun?> ReadStepAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid workflowRunId,
        string stepKey,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction, $"""
            SELECT {StepColumns}
            FROM workflow_steps
            WHERE workflow_run_id = $runId AND step_key = $stepKey;
            """);
        command.Parameters.AddWithValue("$runId", Format(workflowRunId));
        command.Parameters.AddWithValue("$stepKey", stepKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadStep(reader)
            : null;
    }

    private static async ValueTask<WorkflowStepRun?> ReadStepByIdAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid stepId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction, $"""
            SELECT {StepColumns}
            FROM workflow_steps
            WHERE id = $id;
            """);
        command.Parameters.AddWithValue("$id", Format(stepId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadStep(reader)
            : null;
    }

    private static async ValueTask<IReadOnlyList<WorkflowStepRun>> ReadStepsAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        var results = new List<WorkflowStepRun>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            results.Add(ReadStep(reader));
        return results;
    }

    private static WorkflowRun ReadRun(SqliteDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(0)),
        WorkflowName = reader.GetString(1),
        WorkflowVersion = reader.GetString(2),
        Status = (WorkflowStatus)reader.GetInt32(3),
        InputJson = GetNullableString(reader, 4),
        InputType = GetNullableString(reader, 5),
        OutputJson = GetNullableString(reader, 6),
        OutputType = GetNullableString(reader, 7),
        Error = DeserializeError(GetNullableString(reader, 8)),
        CreatedAt = ParseTimestamp(reader.GetString(9)),
        UpdatedAt = ParseTimestamp(reader.GetString(10)),
        CompletedAt = ParseNullableTimestamp(reader, 11),
        Deadline = ParseNullableTimestamp(reader, 12),
        MetadataJson = GetNullableString(reader, 13),
        ParentRunId = reader.IsDBNull(14) ? null : Guid.Parse(reader.GetString(14)),
        LeaseOwner = GetNullableString(reader, 15),
        LeaseExpiresAt = ParseNullableTimestamp(reader, 16)
    };

    private static WorkflowStepRun ReadStep(SqliteDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(0)),
        WorkflowRunId = Guid.Parse(reader.GetString(1)),
        StepKey = reader.GetString(2),
        Status = (StepStatus)reader.GetInt32(3),
        Attempt = reader.GetInt32(4),
        InputJson = GetNullableString(reader, 5),
        InputType = GetNullableString(reader, 6),
        InputHash = GetNullableString(reader, 7),
        OutputJson = GetNullableString(reader, 8),
        OutputType = GetNullableString(reader, 9),
        Error = DeserializeError(GetNullableString(reader, 10)),
        SignalName = GetNullableString(reader, 11),
        CreatedAt = ParseTimestamp(reader.GetString(12)),
        StartedAt = ParseNullableTimestamp(reader, 13),
        CompletedAt = ParseNullableTimestamp(reader, 14),
        AvailableAt = ParseNullableTimestamp(reader, 15),
        LeaseOwner = GetNullableString(reader, 16),
        LeaseExpiresAt = ParseNullableTimestamp(reader, 17)
    };

    private async ValueTask<SqliteConnection> OpenAsync(
        CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            null,
            "PRAGMA foreign_keys = ON;",
            cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            null,
            $"PRAGMA busy_timeout = {(long)options.BusyTimeout.TotalMilliseconds};",
            cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private async ValueTask EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (!initialized)
            await InitializeAsync(cancellationToken).ConfigureAwait(false);
    }

    private static SqliteCommand CreateCommand(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string text)
    {
        var command = connection.CreateCommand();
        command.CommandText = text;
        command.Transaction = transaction;
        return command;
    }

    private static async ValueTask ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string text,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction, text);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<T> ScalarAsync<T>(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string text,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction, text);
        var value = await command.ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false);
        return (T)Convert.ChangeType(value!, typeof(T), CultureInfo.InvariantCulture);
    }

    private static async ValueTask<string> GetCreatedAtAsync(
        SqliteConnection connection,
        Guid workflowRunId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, null, """
            SELECT created_at FROM workflow_runs WHERE id = $id;
            """);
        command.Parameters.AddWithValue("$id", Format(workflowRunId));
        var value = await command.ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false);
        if (value is null)
        {
            throw new KeyNotFoundException(
                $"Workflow '{workflowRunId:D}' does not exist.");
        }
        return (string)value;
    }

    private static void AddRunParameters(SqliteCommand command, WorkflowRun run)
    {
        command.Parameters.AddWithValue("$id", Format(run.Id));
        command.Parameters.AddWithValue("$name", run.WorkflowName);
        command.Parameters.AddWithValue("$version", run.WorkflowVersion);
        command.Parameters.AddWithValue("$status", (int)run.Status);
        command.Parameters.AddWithValue("$inputJson", DbValue(run.InputJson));
        command.Parameters.AddWithValue("$inputType", DbValue(run.InputType));
        command.Parameters.AddWithValue("$outputJson", DbValue(run.OutputJson));
        command.Parameters.AddWithValue("$outputType", DbValue(run.OutputType));
        command.Parameters.AddWithValue("$errorJson", DbValue(SerializeError(run.Error)));
        command.Parameters.AddWithValue("$createdAt", FormatTimestamp(run.CreatedAt));
        command.Parameters.AddWithValue("$updatedAt", FormatTimestamp(run.UpdatedAt));
        command.Parameters.AddWithValue("$completedAt", DbValue(FormatNullable(run.CompletedAt)));
        command.Parameters.AddWithValue("$deadline", DbValue(FormatNullable(run.Deadline)));
        command.Parameters.AddWithValue("$metadataJson", DbValue(run.MetadataJson));
        command.Parameters.AddWithValue(
            "$parentRunId",
            run.ParentRunId is { } parentId ? Format(parentId) : DBNull.Value);
        command.Parameters.AddWithValue("$leaseOwner", DbValue(run.LeaseOwner));
        command.Parameters.AddWithValue("$leaseExpiresAt", DbValue(FormatNullable(run.LeaseExpiresAt)));
    }

    private static void ValidateClaim(StepClaimRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.WorkflowRunId == Guid.Empty)
            throw new ArgumentException("Workflow ID must not be empty.", nameof(request));
        ArgumentException.ThrowIfNullOrWhiteSpace(request.StepKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OutputType);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OwnerId);
        if (request.LeaseExpiresAt <= request.Now)
            throw new ArgumentException("Lease must expire in the future.", nameof(request));
    }

    private static void ValidateStepContract(
        WorkflowStepRun existing,
        StepClaimRequest request)
    {
        if (!string.Equals(existing.InputType, request.InputType, StringComparison.Ordinal) ||
            !string.Equals(existing.InputHash, request.InputHash, StringComparison.Ordinal) ||
            !string.Equals(existing.OutputType, request.OutputType, StringComparison.Ordinal))
        {
            throw new WorkflowStateException(
                $"Step key '{request.StepKey}' was reused with an incompatible input or result contract.");
        }
    }

    private static WorkflowStepRun CancelledPlaceholder(StepClaimRequest request) =>
        new()
        {
            Id = Guid.Empty,
            WorkflowRunId = request.WorkflowRunId,
            StepKey = request.StepKey,
            Status = StepStatus.Cancelled,
            Attempt = 0,
            CreatedAt = request.Now,
            OutputType = request.OutputType
        };

    private static string? SerializeError(WorkflowError? error) =>
        error is null ? null : JsonSerializer.Serialize(error, SerializerOptions);

    private static WorkflowError? DeserializeError(string? json) =>
        json is null
            ? null
            : JsonSerializer.Deserialize<WorkflowError>(json, SerializerOptions);

    private static string? GetNullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static DateTimeOffset? ParseNullableTimestamp(
        SqliteDataReader reader,
        int ordinal) =>
        reader.IsDBNull(ordinal)
            ? null
            : ParseTimestamp(reader.GetString(ordinal));

    private static string Format(Guid value) => value.ToString("D");

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static string? FormatNullable(DateTimeOffset? value) =>
        value is null ? null : FormatTimestamp(value.Value);

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.ParseExact(
            value,
            "O",
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);

    private static object DbValue(string? value) => value ?? (object)DBNull.Value;

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var result = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        result.Converters.Add(new JsonStringEnumConverter());
        return result;
    }

    private const string RunColumns = """
        id, workflow_name, workflow_version, status, input_json, input_type,
        output_json, output_type, error_json, created_at, updated_at,
        completed_at, deadline, metadata_json, parent_run_id,
        lease_owner, lease_expires_at
        """;

    private const string StepColumns = """
        id, workflow_run_id, step_key, status, attempt, input_json, input_type,
        input_hash, output_json, output_type, error_json, signal_name, created_at,
        started_at, completed_at, available_at, lease_owner, lease_expires_at
        """;

    private const string Schema = """
        CREATE TABLE IF NOT EXISTS zhinu_schema(version INTEGER NOT NULL);
        INSERT INTO zhinu_schema(version)
        SELECT 4 WHERE NOT EXISTS (SELECT 1 FROM zhinu_schema);

        CREATE TABLE IF NOT EXISTS workflow_runs
        (
            id TEXT PRIMARY KEY,
            workflow_name TEXT NOT NULL,
            workflow_version TEXT NOT NULL,
            status INTEGER NOT NULL,
            input_json TEXT NULL,
            input_type TEXT NULL,
            output_json TEXT NULL,
            output_type TEXT NULL,
            error_json TEXT NULL,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            completed_at TEXT NULL,
            deadline TEXT NULL,
            metadata_json TEXT NULL,
            parent_run_id TEXT NULL,
            lease_owner TEXT NULL,
            lease_expires_at TEXT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_workflow_runs_runnable
            ON workflow_runs(status, lease_expires_at, created_at);
        CREATE INDEX IF NOT EXISTS ix_workflow_runs_created
            ON workflow_runs(created_at, id);

        CREATE TABLE IF NOT EXISTS workflow_steps
        (
            id TEXT PRIMARY KEY,
            workflow_run_id TEXT NOT NULL,
            step_key TEXT NOT NULL,
            status INTEGER NOT NULL,
            attempt INTEGER NOT NULL,
            input_json TEXT NULL,
            input_type TEXT NULL,
            input_hash TEXT NULL,
            output_json TEXT NULL,
            output_type TEXT NULL,
            error_json TEXT NULL,
            signal_name TEXT NULL,
            created_at TEXT NOT NULL,
            started_at TEXT NULL,
            completed_at TEXT NULL,
            available_at TEXT NULL,
            lease_owner TEXT NULL,
            lease_expires_at TEXT NULL,
            UNIQUE(workflow_run_id, step_key),
            FOREIGN KEY(workflow_run_id) REFERENCES workflow_runs(id) ON DELETE CASCADE
        );
        CREATE INDEX IF NOT EXISTS ix_workflow_steps_run
            ON workflow_steps(workflow_run_id, created_at);
        CREATE INDEX IF NOT EXISTS ix_workflow_steps_runnable
            ON workflow_steps(status, available_at, lease_expires_at);

        CREATE TABLE IF NOT EXISTS workflow_events
        (
            sequence INTEGER PRIMARY KEY AUTOINCREMENT,
            workflow_run_id TEXT NOT NULL,
            step_key TEXT NULL,
            event_type TEXT NOT NULL,
            timestamp TEXT NOT NULL,
            attempt INTEGER NULL,
            data_json TEXT NULL,
            FOREIGN KEY(workflow_run_id) REFERENCES workflow_runs(id) ON DELETE CASCADE
        );
        CREATE INDEX IF NOT EXISTS ix_workflow_events_run_sequence
            ON workflow_events(workflow_run_id, sequence);

        CREATE TABLE IF NOT EXISTS workflow_signals
        (
            id TEXT PRIMARY KEY,
            workflow_run_id TEXT NOT NULL,
            signal_name TEXT NOT NULL,
            data_json TEXT NULL,
            delivered_step_id TEXT NULL,
            created_at TEXT NOT NULL,
            FOREIGN KEY(workflow_run_id) REFERENCES workflow_runs(id) ON DELETE CASCADE
        );
        CREATE INDEX IF NOT EXISTS ix_workflow_signals_run_name
            ON workflow_signals(workflow_run_id, signal_name, delivered_step_id);
        """;

    private const int CurrentSchemaVersion = 4;

    private static async ValueTask MigrateAsync(
        SqliteConnection connection,
        long version,
        CancellationToken cancellationToken)
    {
        while (version < CurrentSchemaVersion)
        {
            version = version switch
            {
                1 => await MigrateV1ToV2Async(connection, cancellationToken)
                    .ConfigureAwait(false),
                2 => await MigrateV2ToV3Async(connection, cancellationToken)
                    .ConfigureAwait(false),
                3 => await MigrateV3ToV4Async(connection, cancellationToken)
                    .ConfigureAwait(false),
                _ => throw new InvalidOperationException(
                    $"No migration path from Zhinu SQLite schema version {version}.")
            };
        }
    }

    private static async ValueTask<long> MigrateV1ToV2Async(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync(
            connection,
            null,
            """
            ALTER TABLE workflow_runs ADD COLUMN deadline TEXT NULL;
            """,
            cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            null,
            "UPDATE zhinu_schema SET version = 2;",
            cancellationToken).ConfigureAwait(false);
        return 2;
    }

    private static async ValueTask<long> MigrateV2ToV3Async(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync(
            connection,
            null,
            """
            ALTER TABLE workflow_runs ADD COLUMN metadata_json TEXT NULL;
            """,
            cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            null,
            "UPDATE zhinu_schema SET version = 3;",
            cancellationToken).ConfigureAwait(false);
        return 3;
    }

    private static async ValueTask<long> MigrateV3ToV4Async(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync(
            connection,
            null,
            """
            ALTER TABLE workflow_runs ADD COLUMN parent_run_id TEXT NULL;
            ALTER TABLE workflow_steps ADD COLUMN signal_name TEXT NULL;
            CREATE INDEX IF NOT EXISTS ix_workflow_runs_parent
                ON workflow_runs(parent_run_id);
            CREATE TABLE IF NOT EXISTS workflow_signals
            (
                id TEXT PRIMARY KEY,
                workflow_run_id TEXT NOT NULL,
                signal_name TEXT NOT NULL,
                data_json TEXT NULL,
                delivered_step_id TEXT NULL,
                created_at TEXT NOT NULL,
                FOREIGN KEY(workflow_run_id) REFERENCES workflow_runs(id) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS ix_workflow_signals_run_name
                ON workflow_signals(workflow_run_id, signal_name, delivered_step_id);
            UPDATE zhinu_schema SET version = 4;
            """,
            cancellationToken).ConfigureAwait(false);
        return 4;
    }
}
