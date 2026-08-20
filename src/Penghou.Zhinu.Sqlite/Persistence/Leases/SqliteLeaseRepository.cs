using Microsoft.Data.Sqlite;
using Penghou.Zhinu.Sqlite.Persistence.Workflows;

namespace Penghou.Zhinu.Sqlite.Persistence.Leases;

/// <summary>Coordinates run and step lease claims, renewals, and recovery.</summary>
internal sealed class SqliteLeaseRepository : IWorkflowLeaseRepository
{
    private readonly SqliteConnectionFactory factory;
    private readonly GetRunnableRunIdsQuery getRunnableRunIds = new();
    private readonly GetRunStatusQuery getRunStatus = new();
    private readonly ClaimRunCommand claimRun = new();
    private readonly RenewRunLeaseCommand renewRunLease = new();
    private readonly ReleaseRunLeaseCommand releaseRunLease = new();
    private readonly GetExpiredStepsQuery getExpiredSteps = new();
    private readonly RecoverExpiredStepsCommand recoverExpiredSteps = new();
    private readonly RecoverExpiredRunsCommand recoverExpiredRuns = new();
    private readonly RecoverExpiredCompensationsCommand recoverExpiredCompensations = new();
    private readonly InsertEventCommand insertEvent = new();

    public SqliteLeaseRepository(SqliteConnectionFactory factory) => this.factory = factory;

    public async ValueTask<IReadOnlyList<Guid>> GetRunnableRunIdsAsync(
        DateTimeOffset now,
        int limit,
        CancellationToken cancellationToken = default)
    {
        await factory.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await factory.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        return await getRunnableRunIds.ExecuteAsync(
            connection,
            now,
            limit,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<long?> TryClaimRunAsync(
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
        var status = await getRunStatus.ExecuteAsync(
            connection,
            transaction,
            workflowRunId,
            cancellationToken).ConfigureAwait(false);
        if (status is not (WorkflowStatus.Pending or WorkflowStatus.Running))
            return null;
        var generation = await claimRun.ExecuteAsync(
            connection,
            transaction,
            workflowRunId,
            ownerId,
            now,
            leaseExpiresAt,
            cancellationToken).ConfigureAwait(false);
        if (generation is null)
            return null;
        if (status == WorkflowStatus.Running)
        {
            await insertEvent.ExecuteAsync(
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
        return generation;
    }

    public async ValueTask<bool> RenewRunLeaseAsync(
        Guid workflowRunId,
        string ownerId,
        DateTimeOffset leaseExpiresAt,
        CancellationToken cancellationToken = default)
    {
        await factory.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await factory.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        return await renewRunLease.ExecuteAsync(
            connection,
            workflowRunId,
            ownerId,
            leaseExpiresAt,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask ReleaseRunLeaseAsync(
        Guid workflowRunId,
        string ownerId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await factory.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await factory.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        await releaseRunLease.ExecuteAsync(
            connection,
            workflowRunId,
            ownerId,
            now,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<int> RecoverExpiredLeasesAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await factory.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await factory.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var expired = await getExpiredSteps.ExecuteAsync(
            connection,
            transaction,
            now,
            cancellationToken).ConfigureAwait(false);
        await recoverExpiredSteps.ExecuteAsync(
            connection,
            transaction,
            now,
            cancellationToken).ConfigureAwait(false);
        await recoverExpiredRuns.ExecuteAsync(
            connection,
            transaction,
            now,
            cancellationToken).ConfigureAwait(false);
        await recoverExpiredCompensations.ExecuteAsync(
            connection,
            transaction,
            now,
            cancellationToken).ConfigureAwait(false);
        foreach (var step in expired)
        {
            await insertEvent.ExecuteAsync(
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
}
