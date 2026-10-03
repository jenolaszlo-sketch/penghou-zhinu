using Microsoft.Data.Sqlite;
using System.Text.Json;
using Penghou.Zhinu.Sqlite.Persistence.Leases;
using Penghou.Zhinu.Sqlite.Persistence.Workflows;

namespace Penghou.Zhinu.Sqlite.Persistence.Steps;

internal sealed partial class SqliteStepRepository
{
    public async ValueTask CreateOperationAsync(
        WorkflowRunOperation operation,
        CancellationToken cancellationToken = default)
    {
        await factory.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await factory.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        await insertOperation.ExecuteAsync(connection, null, operation, cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<long?> TryCreateAndClaimRollbackAndRestartAsync(
        WorkflowRunOperation operation,
        string ownerId,
        DateTimeOffset now,
        DateTimeOffset leaseExpiresAt,
        CancellationToken cancellationToken = default)
    {
        await factory.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await factory.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var generation = await claimRollbackAndRestart.ExecuteAsync(
            connection,
            transaction,
            operation.WorkflowRunId,
            ownerId,
            now,
            leaseExpiresAt,
            cancellationToken).ConfigureAwait(false);
        if (generation is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }
        await insertOperation.ExecuteAsync(
            connection,
            transaction,
            operation,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return generation;
    }

    public async ValueTask<WorkflowRunOperation?> GetActiveOperationAsync(
        Guid workflowRunId,
        CancellationToken cancellationToken = default)
    {
        await factory.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await factory.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        return await getActiveOperation.ExecuteAsync(
            connection,
            workflowRunId,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<bool> UpdateOperationStatusAsync(
        Guid operationId,
        WorkflowOperationStatus status,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await factory.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await factory.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        return await updateOperationStatus.ExecuteAsync(
            connection,
            operationId,
            status,
            now,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<long?> ClaimRollbackAndRestartAsync(
        Guid workflowRunId,
        string ownerId,
        DateTimeOffset now,
        DateTimeOffset leaseExpiresAt,
        CancellationToken cancellationToken = default)
    {
        await factory.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await factory.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var generation = await claimRollbackAndRestart.ExecuteAsync(
            connection,
            transaction,
            workflowRunId,
            ownerId,
            now,
            leaseExpiresAt,
            cancellationToken).ConfigureAwait(false);
        if (generation is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return generation;
    }

    public async ValueTask<bool> RenewRollbackAndRestartLeaseAsync(
        Guid workflowRunId,
        string ownerId,
        DateTimeOffset leaseExpiresAt,
        CancellationToken cancellationToken = default)
    {
        await factory.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await factory.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        return await renewRollbackAndRestartLease.ExecuteAsync(
            connection,
            workflowRunId,
            ownerId,
            leaseExpiresAt,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask ReleaseRollbackAndRestartLeaseAsync(
        Guid workflowRunId,
        string ownerId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await factory.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await factory.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        await releaseRollbackAndRestartLease.ExecuteAsync(
            connection,
            workflowRunId,
            ownerId,
            now,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<bool> CompleteRollbackAndRestartAsync(
        Guid workflowRunId,
        string ownerId,
        long generation,
        Guid operationId,
        IReadOnlyList<string> invalidateStepKeys,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await factory.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await factory.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        if (await resetRunForRollbackAndRestart.ExecuteAsync(
            connection,
            transaction,
            workflowRunId,
            ownerId,
            generation,
            now,
            cancellationToken).ConfigureAwait(false) != 1)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }
        var newGeneration = await getRunLeaseGeneration.ExecuteAsync(
            connection,
            transaction,
            workflowRunId,
            cancellationToken).ConfigureAwait(false);
        foreach (var stepKey in invalidateStepKeys)
        {
            var latest = await getStep.ExecuteAsync(
                connection,
                transaction,
                workflowRunId,
                stepKey,
                cancellationToken).ConfigureAwait(false);
            if (latest is null)
                continue;
            var next = new WorkflowStepRun
            {
                Id = Guid.NewGuid(),
                WorkflowRunId = workflowRunId,
                StepKey = stepKey,
                ImplementationKey = latest.ImplementationKey,
                Status = StepStatus.Pending,
                Attempt = 0,
                CreatedAt = now,
                InputJson = latest.InputJson,
                InputType = latest.InputType,
                InputHash = latest.InputHash,
                OutputType = latest.OutputType,
                SignalName = latest.SignalName,
                Revision = latest.Revision + 1,
                LeaseGeneration = newGeneration,
                AuthorizationDeclarationHash = latest.AuthorizationDeclarationHash
            };
            await insertStep.ExecuteAsync(connection, transaction, next, cancellationToken)
                .ConfigureAwait(false);
        }
        await completeOperation.ExecuteAsync(
            connection,
            transaction,
            operationId,
            now,
            cancellationToken).ConfigureAwait(false);
        await insertEvent.ExecuteAsync(
            connection,
            transaction,
            workflowRunId,
            null,
            WorkflowEventTypes.WorkflowRestarted,
            now,
            null,
            JsonSerializer.Serialize(
                new
                {
                    invalidatedSteps = invalidateStepKeys.Count,
                    leaseGeneration = newGeneration
                },
                SqliteStoreSupport.SerializerOptions),
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async ValueTask FailRollbackAndRestartAsync(
        Guid workflowRunId,
        string ownerId,
        long generation,
        Guid operationId,
        WorkflowError error,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await factory.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await factory.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var released = await failRollbackAndRestart.ExecuteAsync(
            connection,
            transaction,
            workflowRunId,
            ownerId,
            generation,
            error,
            now,
            cancellationToken).ConfigureAwait(false);
        await failOperation.ExecuteAsync(
            connection,
            transaction,
            operationId,
            now,
            cancellationToken).ConfigureAwait(false);
        if (released != 1)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return;
        }
        await insertEvent.ExecuteAsync(
            connection,
            transaction,
            workflowRunId,
            null,
            WorkflowEventTypes.WorkflowFailed,
            now,
            null,
            JsonSerializer.Serialize(error, SqliteStoreSupport.SerializerOptions),
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<WorkflowRunOperation?> GetOperationByIdAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        await using var command = SqliteStoreSupport.CreateCommand(connection, transaction, $"""
            SELECT {SqliteStoreSupport.OperationColumns}
            FROM workflow_run_operations
            WHERE operation_id = $operationId
            LIMIT 1;
            """);
        command.Parameters.AddWithValue(
            "$operationId",
            SqliteStoreSupport.Format(operationId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? SqliteStoreSupport.ReadOperation(reader)
            : null;
    }
}
