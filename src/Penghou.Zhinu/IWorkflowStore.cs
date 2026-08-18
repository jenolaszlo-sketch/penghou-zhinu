namespace Penghou.Zhinu;

/// <summary>
/// Persists current workflow state and transactional diagnostic events.
/// Implementations are responsible for atomic claims and state transitions.
/// </summary>
public interface IWorkflowStore
{
    ValueTask InitializeAsync(CancellationToken cancellationToken = default);

    ValueTask CreateRunAsync(
        WorkflowRun run,
        CancellationToken cancellationToken = default);

    ValueTask<WorkflowRun?> GetRunAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<WorkflowRun>> GetRunsAsync(
        RunQuery query,
        CancellationToken cancellationToken = default);

    ValueTask<WorkflowRun?> UpdateRunMetadataAsync(
        Guid workflowRunId,
        string? metadataJson,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<WorkflowStepRun>> GetStepsAsync(
        Guid workflowRunId,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<WorkflowEvent>> GetEventsAsync(
        Guid workflowRunId,
        long afterSequence,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns <paramref name="workflowRunId"/> and every descendant run
    /// reachable through <see cref="WorkflowRun.ParentRunId"/>, up to
    /// <paramref name="maxDepth"/> levels (the run itself is depth 0), in
    /// creation order. Empty when the run does not exist.
    /// </summary>
    ValueTask<IReadOnlyList<WorkflowRun>> GetRunSubtreeAsync(
        Guid workflowRunId,
        int maxDepth,
        CancellationToken cancellationToken = default);

    ValueTask<WorkflowEvent> AppendEventAsync(
        Guid workflowRunId,
        string eventType,
        string? dataJson,
        string? stepKey = null,
        int? attempt = null,
        CancellationToken cancellationToken = default);

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

    ValueTask CompleteRunAsync(
        Guid workflowRunId,
        string ownerId,
        string? outputJson,
        string outputType,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    ValueTask FailRunAsync(
        Guid workflowRunId,
        string ownerId,
        WorkflowError error,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    ValueTask CancelRunAsync(
        Guid workflowRunId,
        DateTimeOffset now,
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

    ValueTask ScheduleDelayAsync(
        Guid stepId,
        string ownerId,
        DateTimeOffset availableAt,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    ValueTask CompleteDelayAsync(
        Guid stepId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    ValueTask<int> RecoverExpiredLeasesAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    ValueTask<int> PurgeRunsAsync(
        DateTimeOffset olderThan,
        IReadOnlyList<WorkflowStatus>? statuses = null,
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

    ValueTask SendSignalAsync(
        Guid workflowRunId,
        string signalName,
        string? dataJson,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Attempts to complete the signal wait represented by <paramref name="stepId"/>
    /// with the oldest buffered undelivered signal matching
    /// <paramref name="signalName"/>. Returns the delivered payload, or null when
    /// no signal is available yet (the step stays waiting). Freshly claimed
    /// steps are first transitioned to <see cref="StepStatus.Waiting"/>.
    /// </summary>
    ValueTask<SignalDelivery?> TryDeliverSignalAsync(
        Guid stepId,
        string ownerId,
        string signalName,
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
}
