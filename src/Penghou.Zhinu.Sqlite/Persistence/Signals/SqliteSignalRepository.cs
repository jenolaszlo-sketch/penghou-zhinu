using Microsoft.Data.Sqlite;
using System.Text.Json;
using Penghou.Zhinu.Sqlite.Persistence.Steps;
using Penghou.Zhinu.Sqlite.Persistence.Workflows;

namespace Penghou.Zhinu.Sqlite.Persistence.Signals;

/// <summary>Coordinates signal buffering and delivery to waiting steps.</summary>
internal sealed class SqliteSignalRepository : IWorkflowSignalRepository
{
    private const string SignalSendOperationType = "signal-send";
    private readonly IZhinuSqliteDatabase factory;
    private readonly GetRunStatusQuery getRunStatus = new();
    private readonly InsertSignalCommand insertSignal = new();
    private readonly InsertEventCommand insertEvent = new();
    private readonly GetStepByIdQuery getStepById = new();
    private readonly GetUndeliveredSignalQuery getUndeliveredSignal = new();
    private readonly TransitionStepToWaitingCommand transitionStepToWaiting = new();
    private readonly MarkSignalDeliveredCommand markSignalDelivered = new();
    private readonly CompleteStepWithSignalCommand completeStepWithSignal = new();

    public SqliteSignalRepository(IZhinuSqliteDatabase factory) => this.factory = factory;

    public async ValueTask SendSignalAsync(
        Guid workflowRunId,
        string signalName,
        string? dataJson,
        CancellationToken cancellationToken = default)
    {
        if (workflowRunId == Guid.Empty)
            throw new ArgumentException("Workflow ID must not be empty.", nameof(workflowRunId));
        ArgumentException.ThrowIfNullOrWhiteSpace(signalName);
        await factory.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await factory.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var runStatus = await getRunStatus.ExecuteAsync(
            connection,
            transaction,
            workflowRunId,
            cancellationToken).ConfigureAwait(false);
        if (runStatus is null)
        {
            throw new WorkflowNotFoundException(
                $"Workflow '{workflowRunId:D}' does not exist.");
        }
        var now = DateTimeOffset.UtcNow;
        await insertSignal.ExecuteAsync(
            connection,
            transaction,
            Guid.NewGuid(),
            workflowRunId,
            signalName,
            dataJson,
            now,
            cancellationToken).ConfigureAwait(false);
        await insertEvent.ExecuteAsync(
            connection,
            transaction,
            workflowRunId,
            null,
            WorkflowEventTypes.SignalSent,
            now,
            null,
            JsonSerializer.Serialize(
                new { signalName, data = dataJson },
                SqliteStoreSupport.SerializerOptions),
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<SignalSendReceipt> SendSignalIdempotentlyAsync(
        Guid workflowRunId,
        string signalName,
        string? dataJson,
        Guid signalId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        if (workflowRunId == Guid.Empty)
            throw new ArgumentException("Workflow ID must not be empty.", nameof(workflowRunId));
        if (signalId == Guid.Empty)
            throw new ArgumentException("Signal ID must not be empty.", nameof(signalId));
        ArgumentException.ThrowIfNullOrWhiteSpace(signalName);
        await factory.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await factory.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);

        var existing = await GetOperationAsync(
            connection, transaction, signalId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            var receipt = ReadReceipt(
                existing, workflowRunId, signalName, dataJson);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return receipt with { WasBuffered = false };
        }

        var runStatus = await getRunStatus.ExecuteAsync(
            connection,
            transaction,
            workflowRunId,
            cancellationToken).ConfigureAwait(false);
        if (runStatus is null)
        {
            throw new WorkflowNotFoundException(
                $"Workflow '{workflowRunId:D}' does not exist.");
        }
        await insertSignal.ExecuteAsync(
            connection,
            transaction,
            signalId,
            workflowRunId,
            signalName,
            dataJson,
            now,
            cancellationToken).ConfigureAwait(false);
        var signalEvent = await insertEvent.ExecuteAsync(
            connection,
            transaction,
            workflowRunId,
            null,
            WorkflowEventTypes.SignalSent,
            now,
            null,
            JsonSerializer.Serialize(
                new { signalId, signalName, data = dataJson },
                SqliteStoreSupport.SerializerOptions),
            cancellationToken).ConfigureAwait(false);
        var payload = new SignalSendOperationPayload
        {
            SignalName = signalName,
            PayloadIdentity = SignalPayloadIdentity.Compute(dataJson),
            Event = signalEvent
        };
        await using (var command = SqliteStoreSupport.CreateCommand(
            connection,
            transaction,
            """
            INSERT INTO workflow_run_operations
            (operation_id, workflow_run_id, operation_type, status, payload_json,
             created_at, updated_at, completed_at)
            VALUES
            ($operationId, $runId, $operationType, $status, $payloadJson,
             $now, $now, $now);
            """))
        {
            command.Parameters.AddWithValue("$operationId", SqliteStoreSupport.Format(signalId));
            command.Parameters.AddWithValue("$runId", SqliteStoreSupport.Format(workflowRunId));
            command.Parameters.AddWithValue("$operationType", SignalSendOperationType);
            command.Parameters.AddWithValue("$status", (int)WorkflowOperationStatus.Completed);
            command.Parameters.AddWithValue(
                "$payloadJson",
                JsonSerializer.Serialize(payload, SqliteStoreSupport.SerializerOptions));
            command.Parameters.AddWithValue("$now", SqliteStoreSupport.FormatTimestamp(now));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return CreateReceipt(signalId, workflowRunId, payload, wasBuffered: true);
    }

    public async ValueTask<SignalDelivery?> TryDeliverSignalAsync(
        Guid stepId,
        string ownerId,
        string signalName,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(signalName);
        await factory.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await factory.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var step = await getStepById.ExecuteAsync(
            connection,
            transaction,
            stepId,
            cancellationToken).ConfigureAwait(false) ??
            throw new WorkflowNotFoundException($"Step '{stepId:D}' does not exist.");
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
        var (signalId, dataJson) = await getUndeliveredSignal.ExecuteAsync(
            connection,
            transaction,
            step.WorkflowRunId,
            effectiveName,
            cancellationToken).ConfigureAwait(false);
        if (signalId is null)
        {
            if (step.Status == StepStatus.Running)
            {
                await transitionStepToWaiting.ExecuteAsync(
                    connection,
                    transaction,
                    stepId,
                    effectiveName,
                    cancellationToken).ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }
        if (await markSignalDelivered.ExecuteAsync(
            connection,
            transaction,
            signalId,
            stepId,
            cancellationToken).ConfigureAwait(false) != 1)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }
        await completeStepWithSignal.ExecuteAsync(
            connection,
            transaction,
            stepId,
            dataJson,
            effectiveName,
            now,
            cancellationToken).ConfigureAwait(false);
        await insertEvent.ExecuteAsync(
            connection,
            transaction,
            step.WorkflowRunId,
            step.StepKey,
            WorkflowEventTypes.SignalDelivered,
            now,
            step.Attempt,
            JsonSerializer.Serialize(
                new { signalId, signalName = effectiveName, data = dataJson },
                SqliteStoreSupport.SerializerOptions),
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new SignalDelivery(dataJson);
    }

    public async ValueTask<IReadOnlyList<WorkflowSignalRecord>> ListSignalsAsync(
        Guid workflowRunId,
        SignalQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        query.Validate();
        await factory.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await factory.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        DateTimeOffset? cursorCreated = null;
        var conditions = new List<string> { "workflow_run_id = $run" };
        if (query.SignalName is not null)
            conditions.Add("signal_name = $name");
        if (query.Status is { } status)
            conditions.Add(status == SignalStatus.Buffered
                ? "delivered_step_id IS NULL"
                : "delivered_step_id IS NOT NULL");
        if (query.AfterId is not null)
        {
            cursorCreated = await GetCreatedAtAsync(
                connection, query.AfterId.Value, cancellationToken).ConfigureAwait(false);
            if (cursorCreated is null)
                throw new WorkflowNotFoundException($"Signal '{query.AfterId:D}' does not exist.");
            conditions.Add("((created_at > $cursorCreated) OR (created_at = $cursorCreated AND id > $cursorId))");
        }
        var where = string.Join(" AND ", conditions);
        var sql = $"""
            SELECT id, workflow_run_id, signal_name, data_json, delivered_step_id, created_at
            FROM workflow_signals
            WHERE {where}
            ORDER BY created_at, id
            LIMIT $limit;
            """;
        await using var command = SqliteStoreSupport.CreateCommand(connection, null, sql);
        command.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(workflowRunId));
        if (query.SignalName is not null)
            command.Parameters.AddWithValue("$name", query.SignalName);
        if (query.AfterId is not null && cursorCreated is not null)
        {
            command.Parameters.AddWithValue("$cursorCreated", SqliteStoreSupport.FormatTimestamp(cursorCreated.Value));
            command.Parameters.AddWithValue("$cursorId", SqliteStoreSupport.Format(query.AfterId.Value));
        }
        command.Parameters.AddWithValue("$limit", query.Limit);
        var results = new List<WorkflowSignalRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new WorkflowSignalRecord
            {
                Id = Guid.Parse(reader.GetString(0)),
                WorkflowRunId = Guid.Parse(reader.GetString(1)),
                SignalName = reader.GetString(2),
                DataJson = SqliteStoreSupport.GetNullableString(reader, 3),
                DeliveredStepId = reader.IsDBNull(4) ? null : Guid.Parse(reader.GetString(4)),
                CreatedAt = SqliteStoreSupport.ParseTimestamp(reader.GetString(5))
            });
        }
        return results;
    }

    public async ValueTask<int> PurgeSignalsAsync(
        Guid workflowRunId,
        SignalPurgeOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        await factory.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await factory.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        var conditions = new List<string> { "workflow_run_id = $run" };
        if (options.Status is { } status)
            conditions.Add(status == SignalStatus.Buffered
                ? "delivered_step_id IS NULL"
                : "delivered_step_id IS NOT NULL");
        if (options.OlderThan is { } olderThan)
            conditions.Add("created_at < $olderThan");
        var where = string.Join(" AND ", conditions);
        var limitClause = options.Limit is { } limit ? $" LIMIT {limit}" : "";
        var sql = $"DELETE FROM workflow_signals WHERE {where}{limitClause};";
        await using var command = SqliteStoreSupport.CreateCommand(connection, null, sql);
        command.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(workflowRunId));
        if (options.OlderThan is { } older)
            command.Parameters.AddWithValue("$olderThan", SqliteStoreSupport.FormatTimestamp(older));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<DateTimeOffset?> GetCreatedAtAsync(
        SqliteConnection connection, Guid id, CancellationToken ct)
    {
        await using var cmd = SqliteStoreSupport.CreateCommand(connection, null,
            "SELECT created_at FROM workflow_signals WHERE id = $id;");
        cmd.Parameters.AddWithValue("$id", SqliteStoreSupport.Format(id));
        var val = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return val is null or DBNull ? null : SqliteStoreSupport.ParseTimestamp((string)val);
    }

    private static async ValueTask<WorkflowRunOperation?> GetOperationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        await using var command = SqliteStoreSupport.CreateCommand(
            connection,
            transaction,
            $"""
            SELECT {SqliteStoreSupport.OperationColumns}
            FROM workflow_run_operations
            WHERE operation_id = $operationId
            LIMIT 1;
            """);
        command.Parameters.AddWithValue("$operationId", SqliteStoreSupport.Format(operationId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? SqliteStoreSupport.ReadOperation(reader)
            : null;
    }

    private static SignalSendReceipt ReadReceipt(
        WorkflowRunOperation operation,
        Guid workflowRunId,
        string signalName,
        string? dataJson)
    {
        if (operation.WorkflowRunId != workflowRunId ||
            !string.Equals(operation.OperationType, SignalSendOperationType, StringComparison.Ordinal))
        {
            throw SignalConflict(operation.OperationId);
        }
        if (operation.Status != WorkflowOperationStatus.Completed ||
            operation.PayloadJson is null)
        {
            throw new WorkflowStateException(
                $"Signal operation '{operation.OperationId:D}' has no completed receipt.");
        }
        SignalSendOperationPayload payload;
        try
        {
            payload = JsonSerializer.Deserialize<SignalSendOperationPayload>(
                operation.PayloadJson,
                SqliteStoreSupport.SerializerOptions) ??
                throw new JsonException("The signal receipt payload was null.");
        }
        catch (JsonException exception)
        {
            throw new WorkflowStateException(
                $"Signal operation '{operation.OperationId:D}' has an invalid receipt: " +
                exception.Message);
        }
        if (!string.Equals(payload.SignalName, signalName, StringComparison.Ordinal) ||
            !string.Equals(
                payload.PayloadIdentity,
                SignalPayloadIdentity.Compute(dataJson),
                StringComparison.Ordinal))
        {
            throw SignalConflict(operation.OperationId);
        }
        return CreateReceipt(operation.OperationId, workflowRunId, payload, wasBuffered: false);
    }

    private static SignalSendReceipt CreateReceipt(
        Guid signalId,
        Guid workflowRunId,
        SignalSendOperationPayload payload,
        bool wasBuffered) => new()
        {
            SignalId = signalId,
            WorkflowRunId = workflowRunId,
            SignalName = payload.SignalName,
            Event = payload.Event,
            WasBuffered = wasBuffered
        };

    private static WorkflowOperationConflictException SignalConflict(Guid signalId) =>
        new(
            signalId,
            $"Signal ID '{signalId:D}' was already used for different intent.");

    private sealed record SignalSendOperationPayload
    {
        public required string SignalName { get; init; }

        public required string PayloadIdentity { get; init; }

        public required WorkflowEvent Event { get; init; }
    }
}
