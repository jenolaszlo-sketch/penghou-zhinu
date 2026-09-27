using Microsoft.Data.Sqlite;
using Penghou.Zhinu.Sqlite.Persistence;

namespace Penghou.Zhinu.Sqlite;

/// <summary>
/// Durable generic handles linking external provider work to workflow runs.
/// Crash-safe by construction: registration, acquisition, and terminal writes
/// are single transactions, so a lost worker can always re-read the exact
/// durable state and resume, retry, or abandon according to the recorded
/// recovery intent.
/// </summary>
public sealed class SqliteExternalOperationRepository : IWorkflowExternalOperationRepository
{
    private const string Columns = """
        operation_id, workflow_run_id, step_id, step_key, step_revision,
        attempt, idempotency_key, provider, external_id, owner,
        lease_generation, status, recovery_intent, payload_json, error,
        created_at, updated_at, completed_at
        """;

    private readonly SqliteDatabase database;

    /// <summary>Creates a repository over the configured database file.</summary>
    public SqliteExternalOperationRepository(ZhinuSqliteOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        database = new SqliteDatabase(options);
    }

    /// <inheritdoc />
    public async ValueTask<WorkflowExternalOperation> RegisterAsync(
        ExternalOperationRegistration request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Provider);
        if (request.IdempotencyKey is not null &&
            string.IsNullOrWhiteSpace(request.IdempotencyKey))
            throw new ArgumentException(
                "Idempotency key cannot be empty.", nameof(request));
        await database.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await database.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var generation = await ReadRunGenerationAsync(
            connection, transaction, request.WorkflowRunId, cancellationToken)
            .ConfigureAwait(false);
        if (request.IdempotencyKey is not null)
        {
            var existing = await ReadByIdempotencyKeyAsync(
                connection, transaction, request.WorkflowRunId, request.IdempotencyKey,
                cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                if (!SameIntent(existing, request))
                    throw new WorkflowOperationConflictException(
                        existing.OperationId,
                        $"Idempotency key '{request.IdempotencyKey}' was already used for different intent.");
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return existing;
            }
        }

        var now = DateTimeOffset.UtcNow;
        var handle = new WorkflowExternalOperation
        {
            OperationId = Guid.NewGuid(),
            WorkflowRunId = request.WorkflowRunId,
            StepId = request.StepId,
            StepKey = request.StepKey,
            StepRevision = request.StepRevision,
            Attempt = request.Attempt,
            IdempotencyKey = request.IdempotencyKey,
            Provider = request.Provider,
            ExternalId = request.ExternalId,
            OwnerId = null,
            LeaseGeneration = generation,
            Status = ExternalOperationStatus.Requested,
            RecoveryIntent = request.RecoveryIntent,
            PayloadJson = request.PayloadJson,
            CreatedAt = now,
            UpdatedAt = now
        };
        await InsertAsync(connection, transaction, handle, cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return handle;
    }

    /// <inheritdoc />
    public async ValueTask<WorkflowExternalOperation?> GetAsync(
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        await database.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await database.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = SqliteStoreSupport.CreateCommand(
            connection, null,
            $"SELECT {Columns} FROM workflow_external_operations WHERE operation_id = $id;");
        command.Parameters.AddWithValue("$id", SqliteStoreSupport.Format(operationId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? Read(reader)
            : null;
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<WorkflowExternalOperation>> ListAsync(
        Guid workflowRunId,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(limit));
        await database.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await database.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = SqliteStoreSupport.CreateCommand(connection, null, $"""
            SELECT {Columns} FROM workflow_external_operations
            WHERE workflow_run_id = $run
            ORDER BY created_at, operation_id
            LIMIT $limit;
            """);
        command.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(workflowRunId));
        command.Parameters.AddWithValue("$limit", limit);
        var results = new List<WorkflowExternalOperation>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            results.Add(Read(reader));
        return results;
    }

    /// <inheritdoc />
    public async ValueTask<WorkflowExternalOperation> AcquireAsync(
        Guid operationId,
        string ownerId,
        long leaseGeneration,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        await database.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await database.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var current = await ReadAsync(connection, transaction, operationId, cancellationToken)
            .ConfigureAwait(false) ?? throw new WorkflowNotFoundException(
                $"External operation '{operationId:D}' does not exist.");
        var runGeneration = await ReadRunGenerationAsync(
            connection, transaction, current.WorkflowRunId, cancellationToken)
            .ConfigureAwait(false);
        if (runGeneration != leaseGeneration)
        {
            ZhinuDiagnostics.FencingRejectionsCounter.Add(1);
            throw new LeaseLostException(
                $"Caller generation {leaseGeneration} no longer matches run generation " +
                $"{runGeneration}; acquisition refused.");
        }

        await using var command = SqliteStoreSupport.CreateCommand(connection, transaction, """
            UPDATE workflow_external_operations
            SET status = $running, owner = $owner, updated_at = $now
            WHERE operation_id = $id AND status = $requested;
            """);
        command.Parameters.AddWithValue("$running", (int)ExternalOperationStatus.Running);
        command.Parameters.AddWithValue("$owner", ownerId);
        command.Parameters.AddWithValue("$now", SqliteStoreSupport.FormatTimestamp(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("$id", SqliteStoreSupport.Format(operationId));
        command.Parameters.AddWithValue("$requested", (int)ExternalOperationStatus.Requested);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            ZhinuDiagnostics.FencingRejectionsCounter.Add(1);
            throw new LeaseLostException(
                $"External operation '{operationId:D}' is not acquirable in status '{current.Status}'.");
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return (await GetAsync(operationId, cancellationToken).ConfigureAwait(false))!;
    }

    /// <inheritdoc />
    public async ValueTask<WorkflowExternalOperation> CompleteAsync(
        Guid operationId,
        string ownerId,
        string? payloadJson,
        CancellationToken cancellationToken = default) =>
        await FinishAsync(
                operationId, ownerId, ExternalOperationStatus.Completed,
                payloadJson, error: null, cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async ValueTask<WorkflowExternalOperation> FailAsync(
        Guid operationId,
        string ownerId,
        string? error,
        CancellationToken cancellationToken = default) =>
        await FinishAsync(
                operationId, ownerId, ExternalOperationStatus.Failed,
                payloadJson: null, error, cancellationToken)
            .ConfigureAwait(false);

    private async ValueTask<WorkflowExternalOperation> FinishAsync(
        Guid operationId,
        string ownerId,
        ExternalOperationStatus terminal,
        string? payloadJson,
        string? error,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        await database.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await database.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var current = await ReadAsync(connection, transaction, operationId, cancellationToken)
            .ConfigureAwait(false) ?? throw new WorkflowNotFoundException(
                $"External operation '{operationId:D}' does not exist.");

        await using var command = SqliteStoreSupport.CreateCommand(connection, transaction, """
            UPDATE workflow_external_operations
            SET status = $status, payload_json = COALESCE($payload, payload_json),
                error = COALESCE($error, error), updated_at = $now, completed_at = $now
            WHERE operation_id = $id AND status = $running AND owner = $owner;
            """);
        command.Parameters.AddWithValue("$status", (int)terminal);
        command.Parameters.AddWithValue("$payload", SqliteStoreSupport.DbValue(payloadJson));
        command.Parameters.AddWithValue("$error", SqliteStoreSupport.DbValue(error));
        command.Parameters.AddWithValue("$now", SqliteStoreSupport.FormatTimestamp(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("$id", SqliteStoreSupport.Format(operationId));
        command.Parameters.AddWithValue("$running", (int)ExternalOperationStatus.Running);
        command.Parameters.AddWithValue("$owner", ownerId);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            ZhinuDiagnostics.FencingRejectionsCounter.Add(1);
            throw new LeaseLostException(
                $"External operation '{operationId:D}' cannot transition from status " +
                $"'{current.Status}' by this owner.");
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return (await GetAsync(operationId, cancellationToken).ConfigureAwait(false))!;
    }

    private static async ValueTask<WorkflowExternalOperation?> ReadAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        await using var command = SqliteStoreSupport.CreateCommand(
            connection, transaction,
            $"SELECT {Columns} FROM workflow_external_operations WHERE operation_id = $id;");
        command.Parameters.AddWithValue("$id", SqliteStoreSupport.Format(operationId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? Read(reader)
            : null;
    }

    private static async ValueTask<WorkflowExternalOperation?> ReadByIdempotencyKeyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid workflowRunId,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        await using var command = SqliteStoreSupport.CreateCommand(connection, transaction, $"""
            SELECT {Columns} FROM workflow_external_operations
            WHERE workflow_run_id = $run AND idempotency_key = $key;
            """);
        command.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(workflowRunId));
        command.Parameters.AddWithValue("$key", idempotencyKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? Read(reader)
            : null;
    }

    private static async ValueTask<long> ReadRunGenerationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid workflowRunId,
        CancellationToken cancellationToken)
    {
        await using var command = SqliteStoreSupport.CreateCommand(connection, transaction,
            "SELECT lease_generation FROM workflow_runs WHERE id = $id;");
        command.Parameters.AddWithValue("$id", SqliteStoreSupport.Format(workflowRunId));
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (value is null or DBNull)
            throw new WorkflowNotFoundException($"Workflow '{workflowRunId:D}' does not exist.");
        return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async ValueTask InsertAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        WorkflowExternalOperation handle,
        CancellationToken cancellationToken)
    {
        await using var command = SqliteStoreSupport.CreateCommand(connection, transaction, """
            INSERT INTO workflow_external_operations
                (operation_id, workflow_run_id, step_id, step_key, step_revision,
                 attempt, idempotency_key, provider, external_id, owner,
                 lease_generation, status, recovery_intent, payload_json, error,
                 created_at, updated_at, completed_at)
            VALUES
                ($id, $run, $stepId, $stepKey, $stepRevision, $attempt, $key,
                 $provider, $externalId, $owner, $generation, $status, $intent,
                 $payload, $error, $created, $updated, $completed);
            """);
        command.Parameters.AddWithValue("$id", SqliteStoreSupport.Format(handle.OperationId));
        command.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(handle.WorkflowRunId));
        command.Parameters.AddWithValue("$stepId", handle.StepId is null
            ? DBNull.Value : SqliteStoreSupport.Format(handle.StepId.Value));
        command.Parameters.AddWithValue("$stepKey", SqliteStoreSupport.DbValue(handle.StepKey));
        command.Parameters.AddWithValue("$stepRevision", handle.StepRevision ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$attempt", handle.Attempt ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$key", SqliteStoreSupport.DbValue(handle.IdempotencyKey));
        command.Parameters.AddWithValue("$provider", handle.Provider);
        command.Parameters.AddWithValue("$externalId", SqliteStoreSupport.DbValue(handle.ExternalId));
        command.Parameters.AddWithValue("$owner", SqliteStoreSupport.DbValue(handle.OwnerId));
        command.Parameters.AddWithValue("$generation", handle.LeaseGeneration);
        command.Parameters.AddWithValue("$status", (int)handle.Status);
        command.Parameters.AddWithValue("$intent", handle.RecoveryIntent.ToString());
        command.Parameters.AddWithValue("$payload", SqliteStoreSupport.DbValue(handle.PayloadJson));
        command.Parameters.AddWithValue("$error", SqliteStoreSupport.DbValue(handle.Error));
        command.Parameters.AddWithValue("$created", SqliteStoreSupport.FormatTimestamp(handle.CreatedAt));
        command.Parameters.AddWithValue("$updated", SqliteStoreSupport.FormatTimestamp(handle.UpdatedAt));
        command.Parameters.AddWithValue(
            "$completed",
            handle.CompletedAt is null
                ? DBNull.Value
                : SqliteStoreSupport.FormatTimestamp(handle.CompletedAt.Value));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static bool SameIntent(WorkflowExternalOperation existing, ExternalOperationRegistration request) =>
        string.Equals(existing.Provider, request.Provider, StringComparison.Ordinal) &&
        string.Equals(existing.ExternalId, request.ExternalId, StringComparison.Ordinal) &&
        existing.StepId == request.StepId &&
        string.Equals(existing.StepKey, request.StepKey, StringComparison.Ordinal) &&
        existing.StepRevision == request.StepRevision &&
        existing.Attempt == request.Attempt &&
        existing.RecoveryIntent == request.RecoveryIntent &&
        string.Equals(existing.PayloadJson, request.PayloadJson, StringComparison.Ordinal);

    private static WorkflowExternalOperation Read(SqliteDataReader reader) => new()
    {
        OperationId = Guid.Parse(reader.GetString(0)),
        WorkflowRunId = Guid.Parse(reader.GetString(1)),
        StepId = reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2)),
        StepKey = SqliteStoreSupport.GetNullableString(reader, 3),
        StepRevision = reader.IsDBNull(4) ? null : reader.GetInt32(4),
        Attempt = reader.IsDBNull(5) ? null : reader.GetInt32(5),
        IdempotencyKey = SqliteStoreSupport.GetNullableString(reader, 6),
        Provider = reader.GetString(7),
        ExternalId = SqliteStoreSupport.GetNullableString(reader, 8),
        OwnerId = SqliteStoreSupport.GetNullableString(reader, 9),
        LeaseGeneration = reader.GetInt64(10),
        Status = ReadStatus(reader.GetInt32(11)),
        RecoveryIntent = ReadIntent(reader.GetString(12)),
        PayloadJson = SqliteStoreSupport.GetNullableString(reader, 13),
        Error = SqliteStoreSupport.GetNullableString(reader, 14),
        CreatedAt = SqliteStoreSupport.ParseTimestamp(reader.GetString(15)),
        UpdatedAt = SqliteStoreSupport.ParseTimestamp(reader.GetString(16)),
        CompletedAt = reader.IsDBNull(17) ? null : SqliteStoreSupport.ParseTimestamp(reader.GetString(17))
    };

    private static ExternalOperationStatus ReadStatus(int value) =>
        Enum.IsDefined((ExternalOperationStatus)value)
            ? (ExternalOperationStatus)value
            : throw new WorkflowStateException(
                $"Stored external operation status '{value}' is not supported.");

    private static ExternalOperationRecoveryIntent ReadIntent(string value) =>
        Enum.TryParse<ExternalOperationRecoveryIntent>(value, out var intent) &&
        Enum.IsDefined(intent)
            ? intent
            : throw new WorkflowStateException(
                $"Stored external operation recovery intent '{value}' is not supported.");
}
