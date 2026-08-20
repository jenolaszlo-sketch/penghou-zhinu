namespace Penghou.Zhinu;

/// <summary>
/// Persists workflow runs and their transactional diagnostic events.
/// Implementations are responsible for atomic claims and state transitions.
/// </summary>
public interface IWorkflowRepository
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

    ValueTask<int> PurgeRunsAsync(
        DateTimeOffset olderThan,
        IReadOnlyList<WorkflowStatus>? statuses = null,
        CancellationToken cancellationToken = default);
}
