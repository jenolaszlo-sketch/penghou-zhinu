namespace Penghou.Zhinu;

/// <summary>
/// Persists run and step leases and expired-lease recovery.
/// </summary>
public interface IWorkflowLeaseRepository
{
    ValueTask<IReadOnlyList<Guid>> GetRunnableRunIdsAsync(
        DateTimeOffset now,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically claims the run for <paramref name="ownerId"/>, bumping its
    /// fencing generation. Returns the new
    /// <see cref="WorkflowRun.LeaseGeneration"/> on success, or null when the
    /// run cannot be claimed right now.
    /// </summary>
    ValueTask<long?> TryClaimRunAsync(
        Guid workflowRunId,
        string ownerId,
        DateTimeOffset now,
        DateTimeOffset leaseExpiresAt,
        CancellationToken cancellationToken = default);

    ValueTask<bool> RenewRunLeaseAsync(
        Guid workflowRunId,
        string ownerId,
        DateTimeOffset leaseExpiresAt,
        CancellationToken cancellationToken = default);

    ValueTask ReleaseRunLeaseAsync(
        Guid workflowRunId,
        string ownerId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    ValueTask<int> RecoverExpiredLeasesAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default);
}
