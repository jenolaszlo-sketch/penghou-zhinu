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

    ValueTask<bool> TryClaimRunAsync(
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

    ValueTask<int> RestartStepAsync(
        Guid workflowRunId,
        string stepKey,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);
}
