using Microsoft.Data.Sqlite;
using System.Text.Json;
using Penghou.Zhinu.Sqlite.Persistence.Leases;
using Penghou.Zhinu.Sqlite.Persistence.Workflows;

namespace Penghou.Zhinu.Sqlite.Persistence.Steps;

internal sealed partial class SqliteStepRepository
{
    public async ValueTask<WorkflowStepCompensation?> ClaimCompensationAsync(
        Guid workflowRunId,
        string stepKey,
        string ownerId,
        long generation,
        DateTimeOffset now,
        DateTimeOffset leaseExpiresAt,
        string? actor,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        await factory.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await factory.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var claimed = await claimCompensation.ExecuteAsync(
            connection,
            transaction,
            workflowRunId,
            stepKey,
            ownerId,
            generation,
            now,
            leaseExpiresAt,
            actor,
            reason,
            cancellationToken).ConfigureAwait(false);
        if (claimed is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return claimed;
    }

    public async ValueTask CompleteCompensationAsync(
        Guid compensationId,
        string ownerId,
        string? outputJson,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await factory.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await factory.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        if (await completeCompensation.ExecuteAsync(
            connection,
            transaction,
            compensationId,
            ownerId,
            outputJson,
            now,
            cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new WorkflowStateException(
                "Compensation completion requires an owned running lease.");
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask FailCompensationAsync(
        Guid compensationId,
        string ownerId,
        WorkflowError error,
        DateTimeOffset? retryAt,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await factory.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await factory.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        if (await failCompensation.ExecuteAsync(
            connection,
            transaction,
            compensationId,
            ownerId,
            error,
            retryAt,
            now,
            cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new WorkflowStateException(
                "Compensation failure requires an owned running lease.");
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}
