using Microsoft.Data.Sqlite;
using Penghou.Zhinu.Sqlite.Persistence;

namespace Penghou.Zhinu.Sqlite;

/// <summary>
/// Durable workflow-instance identity and generation records. Activation is a
/// single transaction that fences the expected predecessor and establishes
/// the candidate, so a crash can never leave two progression owners.
/// </summary>
public sealed class SqliteWorkflowInstanceRepository : IWorkflowInstanceRepository
{
    private const string Columns = """
        generation_id, instance_id, ordinal, workflow_run_id, plan_revision,
        execution_fingerprint, status, predecessor_generation_id, created_at,
        activated_at, superseded_at
        """;

    private readonly IZhinuSqliteDatabase database;

    /// <summary>Creates a repository sharing the caller's database owner.</summary>
    public SqliteWorkflowInstanceRepository(IZhinuSqliteDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        this.database = database;
    }

    /// <inheritdoc />
    public async ValueTask<WorkflowInstance> CreateInstanceAsync(
        string? metadataJson,
        CancellationToken cancellationToken = default)
    {
        await database.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await database.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        var instance = new WorkflowInstance
        {
            InstanceId = Guid.NewGuid(),
            CreatedAt = DateTimeOffset.UtcNow,
            MetadataJson = metadataJson
        };
        await using var command = SqliteStoreSupport.CreateCommand(connection, null, """
            INSERT INTO workflow_instances (instance_id, created_at, metadata_json)
            VALUES ($id, $created, $metadata);
            """);
        command.Parameters.AddWithValue("$id", SqliteStoreSupport.Format(instance.InstanceId));
        command.Parameters.AddWithValue("$created", SqliteStoreSupport.FormatTimestamp(instance.CreatedAt));
        command.Parameters.AddWithValue("$metadata", SqliteStoreSupport.DbValue(metadataJson));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return instance;
    }

    /// <inheritdoc />
    public async ValueTask<WorkflowInstance?> GetInstanceAsync(
        Guid instanceId,
        CancellationToken cancellationToken = default)
    {
        await database.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await database.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = SqliteStoreSupport.CreateCommand(connection, null,
            "SELECT instance_id, created_at, metadata_json FROM workflow_instances WHERE instance_id = $id;");
        command.Parameters.AddWithValue("$id", SqliteStoreSupport.Format(instanceId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new WorkflowInstance
            {
                InstanceId = Guid.Parse(reader.GetString(0)),
                CreatedAt = SqliteStoreSupport.ParseTimestamp(reader.GetString(1)),
                MetadataJson = SqliteStoreSupport.GetNullableString(reader, 2)
            }
            : null;
    }

    /// <inheritdoc />
    public async ValueTask<WorkflowGeneration> CreateGenerationAsync(
        Guid instanceId,
        Guid workflowRunId,
        string? planRevision,
        string? executionFingerprint,
        Guid? predecessorGenerationId,
        CancellationToken cancellationToken = default)
    {
        await database.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await database.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        if (await ReadInstanceAsync(connection, transaction, instanceId, cancellationToken)
                .ConfigureAwait(false) is null)
            throw new WorkflowNotFoundException($"Workflow instance '{instanceId:D}' does not exist.");
        if (predecessorGenerationId is not null)
        {
            var predecessor = await ReadAsync(
                    connection, transaction, predecessorGenerationId.Value, cancellationToken)
                .ConfigureAwait(false);
            if (predecessor is null || predecessor.InstanceId != instanceId)
                throw new WorkflowStateException(
                    $"Predecessor generation '{predecessorGenerationId:D}' does not belong to instance '{instanceId:D}'.");
        }
        else if (await HasGenerationsAsync(connection, transaction, instanceId, cancellationToken)
            .ConfigureAwait(false))
        {
            throw new WorkflowStateException(
                $"Instance '{instanceId:D}' already has generations; a predecessor is required.");
        }

        var now = DateTimeOffset.UtcNow;
        var generation = new WorkflowGeneration
        {
            GenerationId = Guid.NewGuid(),
            InstanceId = instanceId,
            Ordinal = await NextOrdinalAsync(connection, transaction, instanceId, cancellationToken)
                .ConfigureAwait(false),
            WorkflowRunId = workflowRunId,
            PlanRevision = planRevision,
            ExecutionFingerprint = executionFingerprint,
            Status = WorkflowGenerationStatus.Created,
            PredecessorGenerationId = predecessorGenerationId,
            CreatedAt = now
        };
        await InsertAsync(connection, transaction, generation, cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return generation;
    }

    /// <inheritdoc />
    public async ValueTask<WorkflowGeneration?> GetGenerationAsync(
        Guid generationId,
        CancellationToken cancellationToken = default)
    {
        await database.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await database.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = SqliteStoreSupport.CreateCommand(
            connection, null,
            $"SELECT {Columns} FROM workflow_generations WHERE generation_id = $id;");
        command.Parameters.AddWithValue("$id", SqliteStoreSupport.Format(generationId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? Read(reader)
            : null;
    }

    /// <inheritdoc />
    public async ValueTask<WorkflowGeneration?> GetActiveGenerationAsync(
        Guid instanceId,
        CancellationToken cancellationToken = default)
    {
        await database.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await database.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = SqliteStoreSupport.CreateCommand(connection, null, $"""
            SELECT {Columns} FROM workflow_generations
            WHERE instance_id = $instance AND status IN ($active, $quiescing);
            """);
        command.Parameters.AddWithValue("$instance", SqliteStoreSupport.Format(instanceId));
        command.Parameters.AddWithValue("$active", (int)WorkflowGenerationStatus.Active);
        command.Parameters.AddWithValue("$quiescing", (int)WorkflowGenerationStatus.Quiescing);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? Read(reader)
            : null;
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<WorkflowGeneration>> ListGenerationsAsync(
        Guid instanceId,
        CancellationToken cancellationToken = default)
    {
        await database.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await database.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = SqliteStoreSupport.CreateCommand(connection, null, $"""
            SELECT {Columns} FROM workflow_generations
            WHERE instance_id = $instance
            ORDER BY ordinal;
            """);
        command.Parameters.AddWithValue("$instance", SqliteStoreSupport.Format(instanceId));
        var results = new List<WorkflowGeneration>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            results.Add(Read(reader));
        return results;
    }

    /// <inheritdoc />
    public async ValueTask<WorkflowGeneration> PrepareGenerationAsync(
        Guid generationId,
        CancellationToken cancellationToken = default)
    {
        await database.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await database.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        _ = await ReadAsync(connection, transaction, generationId, cancellationToken)
            .ConfigureAwait(false) ?? throw new WorkflowNotFoundException(
                $"Generation '{generationId:D}' does not exist.");
        await using var command = SqliteStoreSupport.CreateCommand(connection, transaction, """
            UPDATE workflow_generations
            SET status = $prepared
            WHERE generation_id = $id AND status = $created;
            """);
        command.Parameters.AddWithValue("$prepared", (int)WorkflowGenerationStatus.Prepared);
        command.Parameters.AddWithValue("$id", SqliteStoreSupport.Format(generationId));
        command.Parameters.AddWithValue("$created", (int)WorkflowGenerationStatus.Created);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new WorkflowStateException(
                $"Generation '{generationId:D}' is not preparable; only created generations prepare.");
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return (await GetGenerationAsync(generationId, cancellationToken).ConfigureAwait(false))!;
    }

    /// <inheritdoc />
    public async ValueTask<WorkflowGeneration> PauseGenerationAsync(
        Guid generationId,
        CancellationToken cancellationToken = default)
    {
        await database.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await database.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        _ = await ReadAsync(connection, transaction, generationId, cancellationToken)
            .ConfigureAwait(false) ?? throw new WorkflowNotFoundException(
                $"Generation '{generationId:D}' does not exist.");
        await using var command = SqliteStoreSupport.CreateCommand(connection, transaction, """
            UPDATE workflow_generations
            SET status = $quiescing
            WHERE generation_id = $id AND status = $active;
            """);
        command.Parameters.AddWithValue("$quiescing", (int)WorkflowGenerationStatus.Quiescing);
        command.Parameters.AddWithValue("$id", SqliteStoreSupport.Format(generationId));
        command.Parameters.AddWithValue("$active", (int)WorkflowGenerationStatus.Active);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new WorkflowStateException(
                $"Generation '{generationId:D}' is not active; only the active generation pauses.");
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return (await GetGenerationAsync(generationId, cancellationToken).ConfigureAwait(false))!;
    }

    /// <inheritdoc />
    public async ValueTask<WorkflowGeneration> ResumeGenerationAsync(
        Guid generationId,
        CancellationToken cancellationToken = default)
    {
        await database.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await database.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        _ = await ReadAsync(connection, transaction, generationId, cancellationToken)
            .ConfigureAwait(false) ?? throw new WorkflowNotFoundException(
                $"Generation '{generationId:D}' does not exist.");
        await using var command = SqliteStoreSupport.CreateCommand(connection, transaction, """
            UPDATE workflow_generations
            SET status = $active
            WHERE generation_id = $id AND status = $quiescing;
            """);
        command.Parameters.AddWithValue("$active", (int)WorkflowGenerationStatus.Active);
        command.Parameters.AddWithValue("$id", SqliteStoreSupport.Format(generationId));
        command.Parameters.AddWithValue("$quiescing", (int)WorkflowGenerationStatus.Quiescing);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new WorkflowStateException(
                $"Generation '{generationId:D}' is not quiescing; only a quiescing generation " +
                "resumes, and superseded generations never reactivate.");
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return (await GetGenerationAsync(generationId, cancellationToken).ConfigureAwait(false))!;
    }

    /// <inheritdoc />
    public async ValueTask<WorkflowGeneration> ActivateGenerationAsync(
        Guid generationId,
        Guid? expectedPredecessorGenerationId,
        CancellationToken cancellationToken = default)
    {
        await database.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await database.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var candidate = await ReadAsync(connection, transaction, generationId, cancellationToken)
            .ConfigureAwait(false) ?? throw new WorkflowNotFoundException(
                $"Generation '{generationId:D}' does not exist.");
        if (candidate.Status != WorkflowGenerationStatus.Prepared)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new WorkflowStateException(
                $"Generation '{generationId:D}' is '{candidate.Status}', not prepared; activation refused.");
        }
        if (candidate.PredecessorGenerationId != expectedPredecessorGenerationId)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new WorkflowStateException(
                $"Candidate declares a different predecessor than requested; activation refused.");
        }

        var now = DateTimeOffset.UtcNow;
        if (expectedPredecessorGenerationId is null)
        {
            var active = await ReadActiveAsync(
                    connection, transaction, candidate.InstanceId, cancellationToken)
                .ConfigureAwait(false);
            if (active is not null)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                throw new WorkflowStateException(
                    $"Instance already has active generation '{active.GenerationId:D}'; " +
                    "activation refused.");
            }
        }
        else
        {
            await using var supersede = SqliteStoreSupport.CreateCommand(connection, transaction, """
                UPDATE workflow_generations
                SET status = $superseded, superseded_at = $now
                WHERE generation_id = $id AND instance_id = $instance
                    AND status = $quiescing;
                """);
            supersede.Parameters.AddWithValue(
                "$superseded", (int)WorkflowGenerationStatus.Superseded);
            supersede.Parameters.AddWithValue("$now", SqliteStoreSupport.FormatTimestamp(now));
            supersede.Parameters.AddWithValue(
                "$id", SqliteStoreSupport.Format(expectedPredecessorGenerationId.Value));
            supersede.Parameters.AddWithValue(
                "$instance", SqliteStoreSupport.Format(candidate.InstanceId));
            supersede.Parameters.AddWithValue("$quiescing", (int)WorkflowGenerationStatus.Quiescing);
            if (await supersede.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                throw new WorkflowStateException(
                    $"Expected predecessor '{expectedPredecessorGenerationId:D}' is not the " +
                    "quiescing generation; pause the current generation before cutover.");
            }
        }

        await using var activate = SqliteStoreSupport.CreateCommand(connection, transaction, """
            UPDATE workflow_generations
            SET status = $active, activated_at = $now
            WHERE generation_id = $id AND status = $prepared;
            """);
        activate.Parameters.AddWithValue("$active", (int)WorkflowGenerationStatus.Active);
        activate.Parameters.AddWithValue("$now", SqliteStoreSupport.FormatTimestamp(now));
        activate.Parameters.AddWithValue("$id", SqliteStoreSupport.Format(generationId));
        activate.Parameters.AddWithValue("$prepared", (int)WorkflowGenerationStatus.Prepared);
        if (await activate.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new WorkflowStateException(
                $"Generation '{generationId:D}' changed underneath activation.");
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return (await GetGenerationAsync(generationId, cancellationToken).ConfigureAwait(false))!;
    }

    /// <inheritdoc />
    public async ValueTask<WorkflowGeneration> RejectGenerationAsync(
        Guid generationId,
        CancellationToken cancellationToken = default)
    {
        await database.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await database.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        _ = await ReadAsync(connection, transaction, generationId, cancellationToken)
            .ConfigureAwait(false) ?? throw new WorkflowNotFoundException(
                $"Generation '{generationId:D}' does not exist.");
        await using var command = SqliteStoreSupport.CreateCommand(connection, transaction, """
            UPDATE workflow_generations
            SET status = $rejected
            WHERE generation_id = $id
                AND status IN ($created, $prepared);
            """);
        command.Parameters.AddWithValue("$rejected", (int)WorkflowGenerationStatus.Rejected);
        command.Parameters.AddWithValue("$id", SqliteStoreSupport.Format(generationId));
        command.Parameters.AddWithValue("$created", (int)WorkflowGenerationStatus.Created);
        command.Parameters.AddWithValue("$prepared", (int)WorkflowGenerationStatus.Prepared);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new WorkflowStateException(
                $"Generation '{generationId:D}' is not rejectable; active and superseded " +
                "generations stay immutable.");
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return (await GetGenerationAsync(generationId, cancellationToken).ConfigureAwait(false))!;
    }

    /// <inheritdoc />
    public async ValueTask<GenerationDisposition> RecordDispositionAsync(
        Guid generationId,
        CheckpointDisposition disposition,
        string? reason,
        string? actor,
        CancellationToken cancellationToken = default)
    {
        await database.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await database.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        _ = await ReadAsync(connection, transaction, generationId, cancellationToken)
            .ConfigureAwait(false) ?? throw new WorkflowNotFoundException(
                $"Generation '{generationId:D}' does not exist.");
        var recorded = new GenerationDisposition
        {
            DispositionId = Guid.NewGuid(),
            GenerationId = generationId,
            Disposition = disposition,
            Reason = reason,
            Actor = actor,
            CreatedAt = DateTimeOffset.UtcNow
        };
        await using var command = SqliteStoreSupport.CreateCommand(connection, transaction, """
            INSERT INTO workflow_generation_dispositions
                (disposition_id, generation_id, disposition, reason, actor, created_at)
            VALUES
                ($id, $generation, $disposition, $reason, $actor, $created);
            """);
        command.Parameters.AddWithValue("$id", SqliteStoreSupport.Format(recorded.DispositionId));
        command.Parameters.AddWithValue("$generation", SqliteStoreSupport.Format(generationId));
        command.Parameters.AddWithValue("$disposition", (int)disposition);
        command.Parameters.AddWithValue("$reason", SqliteStoreSupport.DbValue(reason));
        command.Parameters.AddWithValue("$actor", SqliteStoreSupport.DbValue(actor));
        command.Parameters.AddWithValue(
            "$created", SqliteStoreSupport.FormatTimestamp(recorded.CreatedAt));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return recorded;
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<GenerationDisposition>> ListDispositionsAsync(
        Guid generationId,
        CancellationToken cancellationToken = default)
    {
        await database.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await database.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = SqliteStoreSupport.CreateCommand(connection, null, """
            SELECT disposition_id, generation_id, disposition, reason, actor, created_at
            FROM workflow_generation_dispositions
            WHERE generation_id = $generation
            ORDER BY rowid;
            """);
        command.Parameters.AddWithValue("$generation", SqliteStoreSupport.Format(generationId));
        var results = new List<GenerationDisposition>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            results.Add(ReadDisposition(reader));
        return results;
    }

    private static GenerationDisposition ReadDisposition(SqliteDataReader reader) => new()
    {
        DispositionId = Guid.Parse(reader.GetString(0)),
        GenerationId = Guid.Parse(reader.GetString(1)),
        Disposition = ReadDispositionValue(reader.GetInt32(2)),
        Reason = SqliteStoreSupport.GetNullableString(reader, 3),
        Actor = SqliteStoreSupport.GetNullableString(reader, 4),
        CreatedAt = SqliteStoreSupport.ParseTimestamp(reader.GetString(5))
    };

    private static CheckpointDisposition ReadDispositionValue(int value) =>
        Enum.IsDefined((CheckpointDisposition)value)
            ? (CheckpointDisposition)value
            : throw new WorkflowStateException(
                $"Stored checkpoint disposition '{value}' is not supported.");

    private static async ValueTask<WorkflowInstance?> ReadInstanceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid instanceId,
        CancellationToken cancellationToken)
    {
        await using var command = SqliteStoreSupport.CreateCommand(
            connection, transaction,
            "SELECT instance_id, created_at, metadata_json FROM workflow_instances WHERE instance_id = $id;");
        command.Parameters.AddWithValue("$id", SqliteStoreSupport.Format(instanceId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new WorkflowInstance
            {
                InstanceId = Guid.Parse(reader.GetString(0)),
                CreatedAt = SqliteStoreSupport.ParseTimestamp(reader.GetString(1)),
                MetadataJson = SqliteStoreSupport.GetNullableString(reader, 2)
            }
            : null;
    }

    private static async ValueTask<bool> HasGenerationsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid instanceId,
        CancellationToken cancellationToken)
    {
        await using var command = SqliteStoreSupport.CreateCommand(connection, transaction,
            "SELECT EXISTS(SELECT 1 FROM workflow_generations WHERE instance_id = $instance);");
        command.Parameters.AddWithValue("$instance", SqliteStoreSupport.Format(instanceId));
        return Convert.ToInt32(
                await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    private static async ValueTask<long> NextOrdinalAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid instanceId,
        CancellationToken cancellationToken)
    {
        await using var command = SqliteStoreSupport.CreateCommand(connection, transaction, """
            SELECT COALESCE(MAX(ordinal), 0) + 1 FROM workflow_generations
            WHERE instance_id = $instance;
            """);
        command.Parameters.AddWithValue("$instance", SqliteStoreSupport.Format(instanceId));
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async ValueTask<WorkflowGeneration?> ReadAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid generationId,
        CancellationToken cancellationToken)
    {
        await using var command = SqliteStoreSupport.CreateCommand(
            connection, transaction,
            $"SELECT {Columns} FROM workflow_generations WHERE generation_id = $id;");
        command.Parameters.AddWithValue("$id", SqliteStoreSupport.Format(generationId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? Read(reader)
            : null;
    }

    private static async ValueTask<WorkflowGeneration?> ReadActiveAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid instanceId,
        CancellationToken cancellationToken)
    {
        await using var command = SqliteStoreSupport.CreateCommand(connection, transaction, $"""
            SELECT {Columns} FROM workflow_generations
            WHERE instance_id = $instance AND status IN ($active, $quiescing);
            """);
        command.Parameters.AddWithValue("$instance", SqliteStoreSupport.Format(instanceId));
        command.Parameters.AddWithValue("$active", (int)WorkflowGenerationStatus.Active);
        command.Parameters.AddWithValue("$quiescing", (int)WorkflowGenerationStatus.Quiescing);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? Read(reader)
            : null;
    }

    private static async ValueTask InsertAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        WorkflowGeneration generation,
        CancellationToken cancellationToken)
    {
        await using var command = SqliteStoreSupport.CreateCommand(connection, transaction, """
            INSERT INTO workflow_generations
                (generation_id, instance_id, ordinal, workflow_run_id, plan_revision,
                 execution_fingerprint, status, predecessor_generation_id, created_at,
                 activated_at, superseded_at)
            VALUES
                ($id, $instance, $ordinal, $run, $revision, $fingerprint, $status,
                 $predecessor, $created, $activated, $superseded);
            """);
        command.Parameters.AddWithValue("$id", SqliteStoreSupport.Format(generation.GenerationId));
        command.Parameters.AddWithValue("$instance", SqliteStoreSupport.Format(generation.InstanceId));
        command.Parameters.AddWithValue("$ordinal", generation.Ordinal);
        command.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(generation.WorkflowRunId));
        command.Parameters.AddWithValue("$revision", SqliteStoreSupport.DbValue(generation.PlanRevision));
        command.Parameters.AddWithValue(
            "$fingerprint", SqliteStoreSupport.DbValue(generation.ExecutionFingerprint));
        command.Parameters.AddWithValue("$status", (int)generation.Status);
        command.Parameters.AddWithValue("$predecessor", generation.PredecessorGenerationId is null
            ? DBNull.Value : SqliteStoreSupport.Format(generation.PredecessorGenerationId.Value));
        command.Parameters.AddWithValue("$created", SqliteStoreSupport.FormatTimestamp(generation.CreatedAt));
        command.Parameters.AddWithValue("$activated", generation.ActivatedAt is null
            ? DBNull.Value : SqliteStoreSupport.FormatTimestamp(generation.ActivatedAt.Value));
        command.Parameters.AddWithValue("$superseded", generation.SupersededAt is null
            ? DBNull.Value : SqliteStoreSupport.FormatTimestamp(generation.SupersededAt.Value));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static WorkflowGeneration Read(SqliteDataReader reader) => new()
    {
        GenerationId = Guid.Parse(reader.GetString(0)),
        InstanceId = Guid.Parse(reader.GetString(1)),
        Ordinal = reader.GetInt64(2),
        WorkflowRunId = Guid.Parse(reader.GetString(3)),
        PlanRevision = SqliteStoreSupport.GetNullableString(reader, 4),
        ExecutionFingerprint = SqliteStoreSupport.GetNullableString(reader, 5),
        Status = ReadStatus(reader.GetInt32(6)),
        PredecessorGenerationId = reader.IsDBNull(7) ? null : Guid.Parse(reader.GetString(7)),
        CreatedAt = SqliteStoreSupport.ParseTimestamp(reader.GetString(8)),
        ActivatedAt = reader.IsDBNull(9) ? null : SqliteStoreSupport.ParseTimestamp(reader.GetString(9)),
        SupersededAt = reader.IsDBNull(10) ? null : SqliteStoreSupport.ParseTimestamp(reader.GetString(10))
    };

    private static WorkflowGenerationStatus ReadStatus(int value) =>
        Enum.IsDefined((WorkflowGenerationStatus)value)
            ? (WorkflowGenerationStatus)value
            : throw new WorkflowStateException(
                $"Stored generation status '{value}' is not supported.");
}
