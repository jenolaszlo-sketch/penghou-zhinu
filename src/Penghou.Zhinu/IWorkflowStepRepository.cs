namespace Penghou.Zhinu;

/// <summary>
/// Persists workflow steps, their dependencies, restarts, rollbacks, and
/// compensations.
/// </summary>
public interface IWorkflowStepRepository
{
    ValueTask<IReadOnlyList<WorkflowStepRun>> GetStepsAsync(
        Guid workflowRunId,
        CancellationToken cancellationToken = default);

    ValueTask<StepClaimResult> ClaimStepAsync(
        StepClaimRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<bool> RenewStepLeaseAsync(
        Guid stepId,
        string ownerId,
        DateTimeOffset leaseExpiresAt,
        CancellationToken cancellationToken = default);

    ValueTask CompleteStepAsync(
        Guid stepId,
        string ownerId,
        string? outputJson,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    ValueTask FailStepAsync(
        Guid stepId,
        string ownerId,
        WorkflowError error,
        DateTimeOffset? retryAt,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<StepDependency>> GetStepDependenciesAsync(
        Guid workflowRunId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns every registered compensation for the run, one row per step
    /// revision, in creation order. Compensations are recorded separately from
    /// step revisions so rollback history stays understandable.
    /// </summary>
    ValueTask<IReadOnlyList<WorkflowStepCompensation>> GetCompensationsAsync(
        Guid workflowRunId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves the set of steps a restart of <paramref name="stepKey"/> would
    /// invalidate under <paramref name="mode"/>, without changing any state.
    /// Throws <see cref="KeyNotFoundException"/> when the run or step does not
    /// exist.
    /// </summary>
    ValueTask<RestartPlan> PlanRestartAsync(
        Guid workflowRunId,
        string stepKey,
        StepRestartMode mode,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Transactionally restarts <paramref name="stepKey"/> under
    /// <paramref name="mode"/>: verifies the run and step, resolves the
    /// invalidation set, bumps the run's fencing generation, inserts a fresh
    /// pending revision for every invalidated step (history is preserved),
    /// resets the run to <see cref="WorkflowStatus.Pending"/>, and emits a
    /// durable restart event. A crash mid-transaction leaves the previous state
    /// fully intact. Returns the plan that was applied.
    /// </summary>
    ValueTask<RestartPlan> RestartStepAsync(
        Guid workflowRunId,
        string stepKey,
        StepRestartMode mode,
        string? actor,
        string? reason,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves the steps a rollback would compensate, without changing any
    /// state. <paramref name="targetStepKey"/> is null for a full rollback;
    /// <paramref name="boundary"/> then has no effect. Throws
    /// <see cref="KeyNotFoundException"/> when the run (or a target step) does
    /// not exist.
    /// </summary>
    ValueTask<RollbackPlan> PlanRollbackAsync(
        Guid workflowRunId,
        string? targetStepKey,
        RollbackBoundary boundary,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically leases a completed or failed run for rollback without
    /// changing its status or fencing generation. Returns the run's current
    /// generation, or null when it is not eligible (still executing, already
    /// compensated, or leased by another rollback).
    /// </summary>
    ValueTask<long?> ClaimRollbackAsync(
        Guid workflowRunId,
        string ownerId,
        DateTimeOffset now,
        DateTimeOffset leaseExpiresAt,
        CancellationToken cancellationToken = default);

    /// <summary>Renews the rollback lease of a completed or failed run.</summary>
    ValueTask<bool> RenewRollbackLeaseAsync(
        Guid workflowRunId,
        string ownerId,
        DateTimeOffset leaseExpiresAt,
        CancellationToken cancellationToken = default);

    /// <summary>Releases a rollback lease, leaving the run's status untouched.</summary>
    ValueTask ReleaseRollbackLeaseAsync(
        Guid workflowRunId,
        string ownerId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks a rolled-back run <see cref="WorkflowStatus.Compensated"/>. Returns
    /// false (and changes nothing) when the run's rollback lease was lost, for
    /// example to a concurrent restart.
    /// </summary>
    ValueTask<bool> CompleteRollbackAsync(
        Guid workflowRunId,
        string ownerId,
        long generation,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Fails a run whose rollback could not compensate every planned step. The
    /// run stays claimable by a later rollback attempt. Best-effort: no-op when
    /// the rollback lease was already lost.
    /// </summary>
    ValueTask FailRollbackAsync(
        Guid workflowRunId,
        string ownerId,
        long generation,
        WorkflowError error,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Claims one pending (or previously failed) compensation for execution,
    /// fencing it to the run's current generation and recording the rollback
    /// actor and reason. Returns the claimed row, or null when it is already
    /// compensated, being handled elsewhere, or not yet eligible for retry.
    /// </summary>
    ValueTask<WorkflowStepCompensation?> ClaimCompensationAsync(
        Guid workflowRunId,
        string stepKey,
        string ownerId,
        long generation,
        DateTimeOffset now,
        DateTimeOffset leaseExpiresAt,
        string? actor,
        string? reason,
        CancellationToken cancellationToken = default);

    /// <summary>Completes an owned, running compensation with its committed result.</summary>
    ValueTask CompleteCompensationAsync(
        Guid compensationId,
        string ownerId,
        string? outputJson,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Fails an owned, running compensation, scheduling a retry at
    /// <paramref name="retryAt"/> when another attempt may run (or marking it
    /// permanently failed when null).
    /// </summary>
    ValueTask FailCompensationAsync(
        Guid compensationId,
        string ownerId,
        WorkflowError error,
        DateTimeOffset? retryAt,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists a durable run-scoped operation (for example rollback-and-restart)
    /// that can be resumed after a crash. The operation row records the intent
    /// (payload) and the current phase.
    /// </summary>
    ValueTask CreateOperationAsync(
        WorkflowRunOperation operation,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically persists a rollback-and-restart operation and claims its run.
    /// If the run cannot be claimed, neither state change is committed.
    /// </summary>
    ValueTask<long?> TryCreateAndClaimRollbackAndRestartAsync(
        WorkflowRunOperation operation,
        string ownerId,
        DateTimeOffset now,
        DateTimeOffset leaseExpiresAt,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the most recently created operation for the run whose status is
    /// still in progress (<see cref="WorkflowOperationStatus.Completed"/> and
    /// <see cref="WorkflowOperationStatus.Failed"/> are excluded), or null when
    /// no such operation exists.
    /// </summary>
    ValueTask<WorkflowRunOperation?> GetActiveOperationAsync(
        Guid workflowRunId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Transitions an operation's durable phase, updating its timestamp. Returns
    /// false when the operation no longer exists.
    /// </summary>
    ValueTask<bool> UpdateOperationStatusAsync(
        Guid operationId,
        WorkflowOperationStatus status,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically claims a completed, failed, or previously rolling-back (lease
    /// expired) run for rollback-and-restart: bumps its fencing generation,
    /// transitions it to <see cref="WorkflowStatus.RollingBack"/>, and takes a
    /// lease. Returns the new generation, or null when the run is not eligible
    /// (still executing, or already compensated).
    /// </summary>
    ValueTask<long?> ClaimRollbackAndRestartAsync(
        Guid workflowRunId,
        string ownerId,
        DateTimeOffset now,
        DateTimeOffset leaseExpiresAt,
        CancellationToken cancellationToken = default);

    /// <summary>Renews the rollback-and-restart lease of a rolling-back run.</summary>
    ValueTask<bool> RenewRollbackAndRestartLeaseAsync(
        Guid workflowRunId,
        string ownerId,
        DateTimeOffset leaseExpiresAt,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Releases a rollback-and-restart lease, leaving the run's rolling-back
    /// status untouched so a later attempt can resume.
    /// </summary>
    ValueTask ReleaseRollbackAndRestartLeaseAsync(
        Guid workflowRunId,
        string ownerId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically rewinds a rolling-back run back to a re-executable state:
    /// verifies the operation still owns the run's lease at
    /// <paramref name="generation"/>, bumps the generation, resets the run to
    /// <see cref="WorkflowStatus.Pending"/> (clearing output, error, and the
    /// lease), inserts a fresh pending revision for every invalidated step,
    /// completes the operation, and emits a durable restart event. Returns false
    /// (changing nothing) when the operation lost its claim, for example to a
    /// concurrent rollback.
    /// </summary>
    ValueTask<bool> CompleteRollbackAndRestartAsync(
        Guid workflowRunId,
        string ownerId,
        long generation,
        Guid operationId,
        IReadOnlyList<string> invalidateStepKeys,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks a rollback-and-restart operation failed, leaving the run claimable
    /// for a later attempt. Best-effort: no-op when the claim was already lost.
    /// </summary>
    ValueTask FailRollbackAndRestartAsync(
        Guid workflowRunId,
        string ownerId,
        long generation,
        Guid operationId,
        WorkflowError error,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);
}
