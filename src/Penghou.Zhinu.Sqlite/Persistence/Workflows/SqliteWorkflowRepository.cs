using Microsoft.Data.Sqlite;
using System.Text.Json;
using Penghou.Zhinu.Sqlite.Persistence.Steps;
using Penghou.Zhinu.Sqlite.Persistence.Waits;

namespace Penghou.Zhinu.Sqlite.Persistence.Workflows;

/// <summary>Coordinates workflow-run and event commands and queries.</summary>
internal sealed class SqliteWorkflowRepository : IWorkflowRepository
{
    private readonly IZhinuSqliteDatabase factory;
    private readonly InsertRunCommand insertRun = new();
    private readonly InsertEventCommand insertEvent = new();
    private readonly UpdateRunMetadataCommand updateRunMetadata = new();
    private readonly AppendEventCommand appendEvent = new();
    private readonly FinishRunCommand finishRun = new();
    private readonly CancelRunCommand cancelRun = new();
    private readonly CancelRunStepsCommand cancelRunSteps = new();
    private readonly PurgeRunsCommand purgeRuns = new();
    private readonly GetRunQuery getRun = new();
    private readonly GetRunsQuery getRuns = new();
    private readonly GetRunSubtreeQuery getRunSubtree = new();
    private readonly GetEventsQuery getEvents = new();

    public SqliteWorkflowRepository(IZhinuSqliteDatabase factory) => this.factory = factory;

    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default) =>
        await factory.InitializeAsync(cancellationToken).ConfigureAwait(false);

    public async ValueTask CreateRunAsync(
        WorkflowRun run,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        await factory.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await factory.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        try
        {
            await insertRun.ExecuteAsync(connection, transaction, run, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (SqliteException exception) when (exception.SqliteExtendedErrorCode == 1555)
        {
            // SQLITE_CONSTRAINT_PRIMARYKEY: another worker won the creation
            // race for this run ID. The engine reconciles identical retries
            // against the admitted record.
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new WorkflowConcurrencyException(
                $"Workflow run '{run.Id:D}' already exists.");
        }
        await insertEvent.ExecuteAsync(
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
        await factory.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await factory.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        return await getRun.ExecuteAsync(connection, null, id, cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<RunRetentionPreview> PreviewRetentionAsync(
        RunRetentionOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        await factory.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await factory.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        var where = EligibilityClause(options);
        await using var countCommand = SqliteStoreSupport.CreateCommand(
            connection, null,
            $"SELECT COUNT(*) FROM workflow_runs WHERE {where};");
        countCommand.Parameters.AddWithValue(
            "$cutoff", SqliteStoreSupport.FormatTimestamp(options.OlderThan));
        var count = Convert.ToInt32(
            await countCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
        await using var sampleCommand = SqliteStoreSupport.CreateCommand(
            connection, null,
            $"SELECT id FROM workflow_runs WHERE {where} " +
            "ORDER BY COALESCE(completed_at, updated_at), id LIMIT 100;");
        sampleCommand.Parameters.AddWithValue(
            "$cutoff", SqliteStoreSupport.FormatTimestamp(options.OlderThan));
        var sample = new List<Guid>();
        await using var reader = await sampleCommand.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            sample.Add(Guid.Parse(reader.GetString(0)));
        return new RunRetentionPreview
        {
            EligibleRunCount = count,
            SampleRunIds = sample,
            EvaluatedAt = factory.TimeProvider.GetUtcNow()
        };
    }

    public async ValueTask<int> PurgeRetainedRunsAsync(
        RunRetentionOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        await factory.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        var where = EligibilityClause(options);
        var deleted = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var connection = await factory.OpenAsync(cancellationToken)
                .ConfigureAwait(false);
            // Eligibility is re-evaluated in every batch: a preview is not a
            // lock, so a run reactivated after previewing is never deleted.
            await using var command = SqliteStoreSupport.CreateCommand(connection, null, $"""
                DELETE FROM workflow_runs WHERE id IN (
                    SELECT id FROM workflow_runs WHERE {where}
                    ORDER BY COALESCE(completed_at, updated_at), id
                    LIMIT $batch);
                """);
            command.Parameters.AddWithValue(
                "$cutoff", SqliteStoreSupport.FormatTimestamp(options.OlderThan));
            command.Parameters.AddWithValue("$batch", options.BatchSize);
            var batch = await command.ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);
            deleted += batch;
            if (batch == 0)
                return deleted;
        }
    }

    public async ValueTask<IReadOnlyList<WorkflowEvent>> ReadExportBatchAsync(
        string consumerId,
        Guid workflowRunId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(consumerId);
        if (limit is < 1 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(limit));
        await factory.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await factory.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var cursorCommand = SqliteStoreSupport.CreateCommand(connection, null, """
            SELECT last_sequence FROM workflow_event_consumers
            WHERE consumer_id = $consumer AND workflow_run_id = $run;
            """);
        cursorCommand.Parameters.AddWithValue("$consumer", consumerId);
        cursorCommand.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(workflowRunId));
        var cursor = await cursorCommand.ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false);
        var after = cursor is null or DBNull
            ? 0L
            : Convert.ToInt64(cursor, System.Globalization.CultureInfo.InvariantCulture);
        await using var batchCommand = SqliteStoreSupport.CreateCommand(connection, null, """
            SELECT sequence, workflow_run_id, step_key, event_type, timestamp, attempt, data_json
            FROM workflow_events
            WHERE workflow_run_id = $run AND sequence > $after
            ORDER BY sequence
            LIMIT $limit;
            """);
        batchCommand.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(workflowRunId));
        batchCommand.Parameters.AddWithValue("$after", after);
        batchCommand.Parameters.AddWithValue("$limit", limit);
        var results = new List<WorkflowEvent>();
        await using var reader = await batchCommand.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            results.Add(ReadEvent(reader));
        return results;
    }

    public async ValueTask AcknowledgeExportAsync(
        string consumerId,
        Guid workflowRunId,
        long sequence,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(consumerId);
        if (sequence < 0)
            throw new ArgumentOutOfRangeException(nameof(sequence));
        await factory.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await factory.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        // Acknowledging a purged or unknown run is a no-op success: there is
        // nothing left to protect, and exporters must not crash-loop on it.
        await using var exists = SqliteStoreSupport.CreateCommand(connection, null, """
            SELECT EXISTS(SELECT 1 FROM workflow_runs WHERE id = $run);
            """);
        exists.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(workflowRunId));
        if (Convert.ToInt32(
                await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture) == 0)
            return;
        var now = factory.TimeProvider.GetUtcNow();
        await using var command = SqliteStoreSupport.CreateCommand(connection, null, """
            INSERT INTO workflow_event_consumers
                (consumer_id, workflow_run_id, last_sequence, updated_at)
            VALUES ($consumer, $run, $sequence, $now)
            ON CONFLICT (consumer_id, workflow_run_id) DO UPDATE SET
                last_sequence = max(workflow_event_consumers.last_sequence, excluded.last_sequence),
                updated_at = excluded.updated_at;
            """);
        command.Parameters.AddWithValue("$consumer", consumerId);
        command.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(workflowRunId));
        command.Parameters.AddWithValue("$sequence", sequence);
        command.Parameters.AddWithValue("$now", SqliteStoreSupport.FormatTimestamp(now));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static WorkflowEvent ReadEvent(Microsoft.Data.Sqlite.SqliteDataReader reader) => new()
    {
        Sequence = reader.GetInt64(0),
        WorkflowRunId = Guid.Parse(reader.GetString(1)),
        StepKey = SqliteStoreSupport.GetNullableString(reader, 2),
        EventType = reader.GetString(3),
        Timestamp = SqliteStoreSupport.ParseTimestamp(reader.GetString(4)),
        Attempt = reader.IsDBNull(5) ? null : reader.GetInt32(5),
        DataJson = SqliteStoreSupport.GetNullableString(reader, 6)
    };

    private static string EligibilityClause(RunRetentionOptions options)
    {
        var statuses = options.Statuses ??
            new[]
            {
                WorkflowStatus.Completed, WorkflowStatus.Failed,
                WorkflowStatus.Cancelled, WorkflowStatus.Compensated
            };
        var values = string.Join(
            ", ",
            statuses.Select(status =>
                ((int)status).ToString(System.Globalization.CultureInfo.InvariantCulture)));
        // Runs with lagging export consumers are skipped: a consumer behind
        // the run's event high-water mark keeps its run until it catches up.
        return $"status IN ({values}) AND COALESCE(completed_at, updated_at) < $cutoff" +
            " AND NOT EXISTS (SELECT 1 FROM workflow_event_consumers lag " +
            "WHERE lag.workflow_run_id = workflow_runs.id AND lag.last_sequence < " +
            "(SELECT COALESCE(MAX(sequence), 0) FROM workflow_events delivered " +
            "WHERE delivered.workflow_run_id = workflow_runs.id))";
    }

    public async ValueTask<IReadOnlyList<WorkflowRun>> GetRunsAsync(
        RunQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        query.Validate();
        await factory.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await factory.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        return await getRuns.ExecuteAsync(connection, query, cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<WorkflowRun?> UpdateRunMetadataAsync(
        Guid workflowRunId,
        string? metadataJson,
        CancellationToken cancellationToken = default)
    {
        if (workflowRunId == Guid.Empty)
            throw new ArgumentException("Workflow ID must not be empty.", nameof(workflowRunId));
        await factory.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await factory.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        if (await updateRunMetadata.ExecuteAsync(
            connection,
            transaction,
            workflowRunId,
            metadataJson,
            DateTimeOffset.UtcNow,
            cancellationToken).ConfigureAwait(false) != 1)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }
        var run = await getRun.ExecuteAsync(
            connection,
            transaction,
            workflowRunId,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return run;
    }

    public async ValueTask<IReadOnlyList<WorkflowRun>> GetRunSubtreeAsync(
        Guid workflowRunId,
        int maxDepth,
        CancellationToken cancellationToken = default)
    {
        if (workflowRunId == Guid.Empty)
            throw new ArgumentException("Workflow ID must not be empty.", nameof(workflowRunId));
        if (maxDepth < 0)
            throw new ArgumentOutOfRangeException(nameof(maxDepth));
        await factory.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await factory.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        return await getRunSubtree.ExecuteAsync(
            connection,
            workflowRunId,
            maxDepth,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<WorkflowEvent>> GetEventsAsync(
        Guid workflowRunId,
        long afterSequence,
        int limit,
        CancellationToken cancellationToken = default)
    {
        await factory.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await factory.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        return await getEvents.ExecuteAsync(
            connection,
            workflowRunId,
            afterSequence,
            limit,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<WorkflowEventPage> ReadEventPageAsync(
        Guid workflowRunId,
        long afterSequence,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (afterSequence < 0)
            throw new ArgumentOutOfRangeException(nameof(afterSequence));
        if (limit is < 1 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(limit));
        await factory.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await factory.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        // Fetch one extra row to report HasMore without a second query.
        var fetched = await getEvents.ExecuteAsync(
            connection,
            workflowRunId,
            afterSequence,
            limit + 1,
            cancellationToken).ConfigureAwait(false);
        var hasMore = fetched.Count > limit;
        IReadOnlyList<WorkflowEvent> events = hasMore ? fetched.Take(limit).ToArray() : fetched;
        var nextCursor = events.Count > 0 ? events[^1].Sequence : afterSequence;
        long pageDurable = 0;
        foreach (var item in events)
        {
            if (item.Durability == WorkflowEventDurability.Durable && item.Sequence > pageDurable)
                pageDurable = item.Sequence;
        }
        // When the page carries no durable event, the durable position is the
        // highest durable sequence at or before the cursor, so it never regresses
        // across pages and advisory events never advance it.
        var throughDurable = pageDurable > 0
            ? pageDurable
            : await GetDurableSequenceAtOrBeforeAsync(
                connection, workflowRunId, afterSequence, cancellationToken).ConfigureAwait(false);
        return new WorkflowEventPage
        {
            Events = events,
            NextCursor = nextCursor,
            HasMore = hasMore,
            ThroughDurableSequence = throughDurable,
            RetentionFloor = 0,
            ResyncRequired = false
        };
    }

    private static async ValueTask<long> GetDurableSequenceAtOrBeforeAsync(
        SqliteConnection connection,
        Guid workflowRunId,
        long sequence,
        CancellationToken cancellationToken)
    {
        await using var command = SqliteStoreSupport.CreateCommand(connection, null, """
            SELECT sequence, event_type
            FROM workflow_events
            WHERE workflow_run_id = $runId AND sequence <= $sequence
            ORDER BY sequence DESC;
            """);
        command.Parameters.AddWithValue("$runId", SqliteStoreSupport.Format(workflowRunId));
        command.Parameters.AddWithValue("$sequence", sequence);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (WorkflowEventTypes.Durability(reader.GetString(1)) == WorkflowEventDurability.Durable)
                return reader.GetInt64(0);
        }
        return 0;
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
        await factory.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await factory.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        return await appendEvent.ExecuteAsync(
            connection,
            workflowRunId,
            eventType,
            dataJson,
            stepKey,
            attempt,
            DateTimeOffset.UtcNow,
            cancellationToken).ConfigureAwait(false);
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
        => await CancelRunAsync(
            workflowRunId,
            actor: null,
            reason: null,
            now,
            cancellationToken).ConfigureAwait(false);

    public async ValueTask CancelRunAsync(
        Guid workflowRunId,
        string? actor,
        string? reason,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await factory.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await factory.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var run = await getRun.ExecuteAsync(
            connection,
            transaction,
            workflowRunId,
            cancellationToken).ConfigureAwait(false);
        if (run is not null &&
            run.Status is WorkflowStatus.Pending or WorkflowStatus.Running)
        {
            RunStateMachine.AssertCanTransition(run.Status, WorkflowStatus.Cancelled, workflowRunId);
        }
        if (await cancelRun.ExecuteAsync(
            connection,
            transaction,
            workflowRunId,
            now,
            cancellationToken).ConfigureAwait(false) == 0)
        {
            return;
        }
        await cancelRunSteps.ExecuteAsync(
            connection,
            transaction,
            workflowRunId,
            now,
            cancellationToken).ConfigureAwait(false);
        await SqliteWaitRepository.CancelRunWaitsAsync(
            connection,
            transaction,
            workflowRunId,
            now,
            cancellationToken).ConfigureAwait(false);
        await insertEvent.ExecuteAsync(
            connection,
            transaction,
            workflowRunId,
            null,
            WorkflowEventTypes.WorkflowCancelled,
            now,
            null,
            actor is null && reason is null
                ? null
                : JsonSerializer.Serialize(new { actor, reason }),
            cancellationToken).ConfigureAwait(false);
        await SqliteWaitRepository.MarkChildWaitsReadyAsync(
                connection,
                transaction,
                workflowRunId,
                now,
                cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<int> PurgeRunsAsync(
        DateTimeOffset olderThan,
        IReadOnlyList<WorkflowStatus>? statuses = null,
        CancellationToken cancellationToken = default)
    {
        await factory.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await factory.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var deleted = await purgeRuns.ExecuteAsync(
            connection,
            transaction,
            olderThan,
            statuses,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return deleted;
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
        await factory.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await factory.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var run = await getRun.ExecuteAsync(
            connection,
            transaction,
            workflowRunId,
            cancellationToken).ConfigureAwait(false);
        if (run is not null)
        {
            RunStateMachine.AssertCanTransition(run.Status, status, workflowRunId);
        }
        if (await finishRun.ExecuteAsync(
            connection,
            transaction,
            workflowRunId,
            ownerId,
            status,
            outputJson,
            outputType,
            error,
            now,
            cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new WorkflowStateException("Workflow completion requires an owned running lease.");
        }
        await insertEvent.ExecuteAsync(
            connection,
            transaction,
            workflowRunId,
            null,
            eventType,
            now,
            null,
            error is null
                ? null
                : JsonSerializer.Serialize(error, SqliteStoreSupport.SerializerOptions),
            cancellationToken).ConfigureAwait(false);
        await SqliteWaitRepository.MarkChildWaitsReadyAsync(
                connection,
                transaction,
                workflowRunId,
                now,
                cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}
