namespace Penghou.Zhinu;

/// <summary>
/// Persists workflow-instance identity separately from run identity, plus an
/// immutable generation record per admitted plan revision. Exactly one
/// generation owns forward progression per instance; activation atomically
/// fences the predecessor so a crash can never leave two owners and never
/// infers authority from partially completed side effects.
/// </summary>
public interface IWorkflowInstanceRepository
{
    /// <summary>Creates a workflow instance.</summary>
    ValueTask<WorkflowInstance> CreateInstanceAsync(
        string? metadataJson,
        CancellationToken cancellationToken = default);

    /// <summary>Finds an instance by identity.</summary>
    ValueTask<WorkflowInstance?> GetInstanceAsync(
        Guid instanceId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a candidate generation bound to a run. Ordinals are assigned
    /// densely per instance; the first generation of an instance may omit
    /// its predecessor.
    /// </summary>
    ValueTask<WorkflowGeneration> CreateGenerationAsync(
        Guid instanceId,
        Guid workflowRunId,
        string? planRevision,
        string? executionFingerprint,
        Guid? predecessorGenerationId,
        CancellationToken cancellationToken = default);

    /// <summary>Finds a generation by identity.</summary>
    ValueTask<WorkflowGeneration?> GetGenerationAsync(
        Guid generationId,
        CancellationToken cancellationToken = default);

    /// <summary>Finds the active generation of an instance, if any.</summary>
    ValueTask<WorkflowGeneration?> GetActiveGenerationAsync(
        Guid instanceId,
        CancellationToken cancellationToken = default);

    /// <summary>Lists an instance's generations in ordinal order.</summary>
    ValueTask<IReadOnlyList<WorkflowGeneration>> ListGenerationsAsync(
        Guid instanceId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks a created generation prepared. Only the recorded generation
    /// moves; anything else fails with <see cref="WorkflowStateException"/>.
    /// </summary>
    ValueTask<WorkflowGeneration> PrepareGenerationAsync(
        Guid generationId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically supersedes the expected active predecessor (or nothing for
    /// a first generation) and activates the prepared candidate. Any other
    /// state fails closed; a superseded generation never becomes active
    /// again. Failed pre-cutover candidates stay resumable on the current
    /// generation via <see cref="RejectGenerationAsync"/>.
    /// </summary>
    ValueTask<WorkflowGeneration> ActivateGenerationAsync(
        Guid generationId,
        Guid? expectedPredecessorGenerationId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Rejects a created or prepared candidate. Active or superseded
    /// generations cannot be rejected.
    /// </summary>
    ValueTask<WorkflowGeneration> RejectGenerationAsync(
        Guid generationId,
        CancellationToken cancellationToken = default);
}
