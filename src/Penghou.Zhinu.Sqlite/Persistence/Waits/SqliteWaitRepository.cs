using Microsoft.Data.Sqlite;

namespace Penghou.Zhinu.Sqlite.Persistence.Waits;

/// <summary>Durable wait records backing parked executions.</summary>
internal sealed class SqliteWaitRepository : IWorkflowWaitRepository
{
    private const string Columns = """
        wait_id, workflow_run_id, step_key, step_revision, step_id, kind,
        signal_name, child_run_id, deadline_at, available_at, status,
        lease_generation, created_at, updated_at
        """;

    private readonly IZhinuSqliteDatabase database;

    public SqliteWaitRepository(IZhinuSqliteDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        this.database = database;
    }

    /// <inheritdoc />
    public async ValueTask<WorkflowWait> ParkWaitAsync(
        ParkWaitRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.StepKey);
        await database.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await database.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var runState = await ReadRunStateAsync(
                connection, transaction, request.WorkflowRunId, cancellationToken)
            .ConfigureAwait(false) ?? throw new WorkflowNotFoundException(
                $"Workflow '{request.WorkflowRunId:D}' does not exist.");
        if (runState.Generation != request.LeaseGeneration)
            throw new LeaseLostException(
                $"Workflow '{request.WorkflowRunId:D}' generation {request.LeaseGeneration} " +
                $"no longer matches the current generation {runState.Generation}; parking refused.");
        if (runState.Status is not (WorkflowStatus.Pending or WorkflowStatus.Running))
            throw new WorkflowStateException(
                $"Workflow '{request.WorkflowRunId:D}' is not executable in state '{runState.Status}'; parking refused.");
        var existing = await ReadAsync(
                connection, transaction, request.WorkflowRunId, request.StepKey, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null && existing.StepRevision > request.StepRevision)
            throw new WorkflowStateException(
                $"Step '{request.StepKey}' already waits at revision {existing.StepRevision}; " +
                "a stale revision cannot park.");
        var deadline = existing?.DeadlineAt ??
            (request.Timeout is null ? null : request.Now + request.Timeout.Value);
        await using var delete = SqliteStoreSupport.CreateCommand(connection, transaction, """
            DELETE FROM workflow_waits
            WHERE workflow_run_id = $run AND step_key = $key;
            """);
        delete.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(request.WorkflowRunId));
        delete.Parameters.AddWithValue("$key", request.StepKey);
        await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        var wait = new WorkflowWait
        {
            WaitId = Guid.NewGuid(),
            WorkflowRunId = request.WorkflowRunId,
            StepKey = request.StepKey,
            StepRevision = request.StepRevision,
            StepId = request.StepId,
            Kind = request.Kind,
            SignalName = request.SignalName,
            ChildRunId = request.ChildRunId,
            DeadlineAt = deadline,
            AvailableAt = request.AvailableAt,
            Status = WaitStatus.Parked,
            LeaseGeneration = request.LeaseGeneration,
            CreatedAt = request.Now,
            UpdatedAt = request.Now
        };
        await using var insert = SqliteStoreSupport.CreateCommand(connection, transaction, """
            INSERT INTO workflow_waits
                (wait_id, workflow_run_id, step_key, step_revision, step_id, kind,
                 signal_name, child_run_id, deadline_at, available_at, status,
                 lease_generation, created_at, updated_at)
            VALUES
                ($id, $run, $key, $revision, $step, $kind, $signal, $child,
                 $deadline, $available, $status, $generation, $created, $updated);
            """);
        insert.Parameters.AddWithValue("$id", SqliteStoreSupport.Format(wait.WaitId));
        insert.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(wait.WorkflowRunId));
        insert.Parameters.AddWithValue("$key", wait.StepKey);
        insert.Parameters.AddWithValue("$revision", wait.StepRevision);
        insert.Parameters.AddWithValue("$step", SqliteStoreSupport.Format(wait.StepId));
        insert.Parameters.AddWithValue("$kind", (int)wait.Kind);
        insert.Parameters.AddWithValue("$signal", SqliteStoreSupport.DbValue(wait.SignalName));
        insert.Parameters.AddWithValue(
            "$child",
            wait.ChildRunId is null ? DBNull.Value : SqliteStoreSupport.Format(wait.ChildRunId.Value));
        insert.Parameters.AddWithValue(
            "$deadline",
            wait.DeadlineAt is null ? DBNull.Value : SqliteStoreSupport.FormatTimestamp(wait.DeadlineAt.Value));
        insert.Parameters.AddWithValue(
            "$available",
            wait.AvailableAt is null ? DBNull.Value : SqliteStoreSupport.FormatTimestamp(wait.AvailableAt.Value));
        insert.Parameters.AddWithValue("$status", (int)wait.Status);
        insert.Parameters.AddWithValue("$generation", wait.LeaseGeneration);
        insert.Parameters.AddWithValue("$created", SqliteStoreSupport.FormatTimestamp(wait.CreatedAt));
        insert.Parameters.AddWithValue("$updated", SqliteStoreSupport.FormatTimestamp(wait.UpdatedAt));
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return wait;
    }

    /// <inheritdoc />
    public async ValueTask<WorkflowWait?> GetWaitAsync(
        Guid workflowRunId,
        string stepKey,
        CancellationToken cancellationToken = default)
    {
        await database.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await database.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        return await ReadAsync(connection, null, workflowRunId, stepKey, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<WorkflowWait>> ListWaitsAsync(
        Guid workflowRunId,
        CancellationToken cancellationToken = default)
    {
        await database.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await database.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = SqliteStoreSupport.CreateCommand(connection, null, $"""
            SELECT {Columns} FROM workflow_waits
            WHERE workflow_run_id = $run
            ORDER BY step_key;
            """);
        command.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(workflowRunId));
        var results = new List<WorkflowWait>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            results.Add(Read(reader));
        return results;
    }

    /// <inheritdoc />
    public async ValueTask CompleteWaitAsync(
        Guid waitId,
        CancellationToken cancellationToken = default)
    {
        await database.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await database.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = SqliteStoreSupport.CreateCommand(connection, null, """
            UPDATE workflow_waits
            SET status = $completed
            WHERE wait_id = $id AND status IN ($parked, $ready);
            """);
        command.Parameters.AddWithValue("$completed", (int)WaitStatus.Completed);
        command.Parameters.AddWithValue("$id", SqliteStoreSupport.Format(waitId));
        command.Parameters.AddWithValue("$parked", (int)WaitStatus.Parked);
        command.Parameters.AddWithValue("$ready", (int)WaitStatus.Ready);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask MarkSignalWaitsReadyAsync(
        Guid workflowRunId,
        string signalName,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await database.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await database.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = SqliteStoreSupport.CreateCommand(connection, null, """
            UPDATE workflow_waits
            SET status = $ready, updated_at = $now
            WHERE workflow_run_id = $run AND signal_name = $signal
                AND kind = $kind AND status = $parked;
            """);
        command.Parameters.AddWithValue("$ready", (int)WaitStatus.Ready);
        command.Parameters.AddWithValue("$now", SqliteStoreSupport.FormatTimestamp(now));
        command.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(workflowRunId));
        command.Parameters.AddWithValue("$signal", signalName);
        command.Parameters.AddWithValue("$kind", (int)WaitKind.Signal);
        command.Parameters.AddWithValue("$parked", (int)WaitStatus.Parked);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask MarkChildWaitsReadyAsync(
        Guid childRunId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await database.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await database.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = SqliteStoreSupport.CreateCommand(connection, null, """
            UPDATE workflow_waits
            SET status = $ready, updated_at = $now
            WHERE child_run_id = $child AND kind = $kind AND status = $parked;
            """);
        command.Parameters.AddWithValue("$ready", (int)WaitStatus.Ready);
        command.Parameters.AddWithValue("$now", SqliteStoreSupport.FormatTimestamp(now));
        command.Parameters.AddWithValue("$child", SqliteStoreSupport.Format(childRunId));
        command.Parameters.AddWithValue("$kind", (int)WaitKind.Child);
        command.Parameters.AddWithValue("$parked", (int)WaitStatus.Parked);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<bool> HasParkedWaitsAsync(
        Guid workflowRunId,
        long leaseGeneration,
        CancellationToken cancellationToken = default)
    {
        await database.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await database.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = SqliteStoreSupport.CreateCommand(connection, null, """
            SELECT EXISTS(
                SELECT 1 FROM workflow_waits
                WHERE workflow_run_id = $run AND status = $parked
                    AND lease_generation = $generation);
            """);
        command.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(workflowRunId));
        command.Parameters.AddWithValue("$parked", (int)WaitStatus.Parked);
        command.Parameters.AddWithValue("$generation", leaseGeneration);
        return Convert.ToInt32(
                await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    private async ValueTask<(long Generation, WorkflowStatus Status)?> ReadRunStateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid workflowRunId,
        CancellationToken cancellationToken)
    {
        await using var command = SqliteStoreSupport.CreateCommand(connection, transaction, """
            SELECT lease_generation, status FROM workflow_runs WHERE id = $id;
            """);
        command.Parameters.AddWithValue("$id", SqliteStoreSupport.Format(workflowRunId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? (reader.GetInt64(0), (WorkflowStatus)reader.GetInt32(1))
            : null;
    }

    private static async ValueTask<WorkflowWait?> ReadAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid workflowRunId,
        string stepKey,
        CancellationToken cancellationToken)
    {
        await using var command = SqliteStoreSupport.CreateCommand(
            connection, transaction,
            $"SELECT {Columns} FROM workflow_waits WHERE workflow_run_id = $run AND step_key = $key;");
        command.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(workflowRunId));
        command.Parameters.AddWithValue("$key", stepKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? Read(reader)
            : null;
    }

    /// <summary>Marks the latest wait for a step completed inside the caller's transaction.</summary>
    internal static async ValueTask CompleteStepWaitAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid workflowRunId,
        string stepKey,
        CancellationToken cancellationToken)
    {
        await using var command = SqliteStoreSupport.CreateCommand(connection, transaction, """
            UPDATE workflow_waits
            SET status = $completed
            WHERE workflow_run_id = $run AND step_key = $key
                AND status IN ($parked, $ready);
            """);
        command.Parameters.AddWithValue("$completed", (int)WaitStatus.Completed);
        command.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(workflowRunId));
        command.Parameters.AddWithValue("$key", stepKey);
        command.Parameters.AddWithValue("$parked", (int)WaitStatus.Parked);
        command.Parameters.AddWithValue("$ready", (int)WaitStatus.Ready);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Flips parked signal waits to ready inside the caller's transaction.</summary>
    internal static async ValueTask MarkSignalReadyInTransactionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid workflowRunId,
        string signalName,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = SqliteStoreSupport.CreateCommand(connection, transaction, """
            UPDATE workflow_waits
            SET status = $ready, updated_at = $now
            WHERE workflow_run_id = $run AND signal_name = $signal
                AND kind = $kind AND status = $parked;
            """);
        command.Parameters.AddWithValue("$ready", (int)WaitStatus.Ready);
        command.Parameters.AddWithValue("$now", SqliteStoreSupport.FormatTimestamp(now));
        command.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(workflowRunId));
        command.Parameters.AddWithValue("$signal", signalName);
        command.Parameters.AddWithValue("$kind", (int)WaitKind.Signal);
        command.Parameters.AddWithValue("$parked", (int)WaitStatus.Parked);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Cancels active waits with their run in the caller's transaction.</summary>
    internal static async ValueTask CancelRunWaitsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid workflowRunId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = SqliteStoreSupport.CreateCommand(connection, transaction, """
            UPDATE workflow_waits
            SET status = $cancelled, updated_at = $now
            WHERE workflow_run_id = $run AND status IN ($parked, $ready);
            """);
        command.Parameters.AddWithValue("$cancelled", (int)WaitStatus.Cancelled);
        command.Parameters.AddWithValue("$now", SqliteStoreSupport.FormatTimestamp(now));
        command.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(workflowRunId));
        command.Parameters.AddWithValue("$parked", (int)WaitStatus.Parked);
        command.Parameters.AddWithValue("$ready", (int)WaitStatus.Ready);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Marks one wait cancelled inside the caller's transaction.</summary>
    internal static async ValueTask CancelWaitInTransactionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid waitId,
        CancellationToken cancellationToken)
    {
        await using var command = SqliteStoreSupport.CreateCommand(connection, transaction, """
            UPDATE workflow_waits
            SET status = $cancelled
            WHERE wait_id = $id AND status IN ($parked, $ready);
            """);
        command.Parameters.AddWithValue("$cancelled", (int)WaitStatus.Cancelled);
        command.Parameters.AddWithValue("$id", SqliteStoreSupport.Format(waitId));
        command.Parameters.AddWithValue("$parked", (int)WaitStatus.Parked);
        command.Parameters.AddWithValue("$ready", (int)WaitStatus.Ready);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Flips parked child waits to ready inside the caller's transaction.</summary>
    internal static async ValueTask MarkChildWaitsReadyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid childRunId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = SqliteStoreSupport.CreateCommand(connection, transaction, """
            UPDATE workflow_waits
            SET status = $ready, updated_at = $now
            WHERE child_run_id = $child AND kind = $kind AND status = $parked;
            """);
        command.Parameters.AddWithValue("$ready", (int)WaitStatus.Ready);
        command.Parameters.AddWithValue("$now", SqliteStoreSupport.FormatTimestamp(now));
        command.Parameters.AddWithValue("$child", SqliteStoreSupport.Format(childRunId));
        command.Parameters.AddWithValue("$kind", (int)WaitKind.Child);
        command.Parameters.AddWithValue("$parked", (int)WaitStatus.Parked);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads the latest wait for a step key inside the caller's transaction.</summary>
    internal static async ValueTask<WorkflowWait?> ReadStepWaitAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid workflowRunId,
        string stepKey,
        CancellationToken cancellationToken)
    {
        await using var command = SqliteStoreSupport.CreateCommand(
            connection, transaction,
            $"SELECT {Columns} FROM workflow_waits WHERE workflow_run_id = $run AND step_key = $key;");
        command.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(workflowRunId));
        command.Parameters.AddWithValue("$key", stepKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? Read(reader)
            : null;
    }

    private static WorkflowWait Read(SqliteDataReader reader) => new()
    {
        WaitId = Guid.Parse(reader.GetString(0)),
        WorkflowRunId = Guid.Parse(reader.GetString(1)),
        StepKey = reader.GetString(2),
        StepRevision = reader.GetInt32(3),
        StepId = Guid.Parse(reader.GetString(4)),
        Kind = ReadKind(reader.GetInt32(5)),
        SignalName = SqliteStoreSupport.GetNullableString(reader, 6),
        ChildRunId = reader.IsDBNull(7) ? null : Guid.Parse(reader.GetString(7)),
        DeadlineAt = reader.IsDBNull(8) ? null : SqliteStoreSupport.ParseTimestamp(reader.GetString(8)),
        AvailableAt = reader.IsDBNull(9) ? null : SqliteStoreSupport.ParseTimestamp(reader.GetString(9)),
        Status = ReadStatus(reader.GetInt32(10)),
        LeaseGeneration = reader.GetInt64(11),
        CreatedAt = SqliteStoreSupport.ParseTimestamp(reader.GetString(12)),
        UpdatedAt = SqliteStoreSupport.ParseTimestamp(reader.GetString(13))
    };

    private static WaitKind ReadKind(int value) =>
        Enum.IsDefined((WaitKind)value)
            ? (WaitKind)value
            : throw new WorkflowStateException($"Stored wait kind '{value}' is not supported.");

    private static WaitStatus ReadStatus(int value) =>
        Enum.IsDefined((WaitStatus)value)
            ? (WaitStatus)value
            : throw new WorkflowStateException($"Stored wait status '{value}' is not supported.");
}
