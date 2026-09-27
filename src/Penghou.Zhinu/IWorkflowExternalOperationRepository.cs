namespace Penghou.Zhinu;

/// <summary>
/// Persists generic handles linking external provider work to workflow runs.
/// Unlike <see cref="WorkflowRunOperation"/>, which is reserved for run
/// maintenance and signal receipts, these handles carry step
/// execution/revision linkage, attempt or idempotency identity,
/// owner/generation fencing, lifecycle status, and recovery intent so a
/// crashed worker can resume exactly where the external call stopped.
/// </summary>
public interface IWorkflowExternalOperationRepository
{
    /// <summary>
    /// Registers a handle in <see cref="ExternalOperationStatus.Requested"/>.
    /// Re-registering an idempotency key returns the original record; a key
    /// reused with a different provider, external id, or step link fails with
    /// <see cref="WorkflowOperationConflictException"/>.
    /// </summary>
    ValueTask<WorkflowExternalOperation> RegisterAsync(
        ExternalOperationRegistration request,
        CancellationToken cancellationToken = default);

    /// <summary>Finds a handle by its operation identity.</summary>
    ValueTask<WorkflowExternalOperation?> GetAsync(
        Guid operationId,
        CancellationToken cancellationToken = default);

    /// <summary>Lists a run's handles oldest first, bounded by limit.</summary>
    ValueTask<IReadOnlyList<WorkflowExternalOperation>> ListAsync(
        Guid workflowRunId,
        int limit = 100,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Claims a requested handle for an owner. Only the transition from
    /// <see cref="ExternalOperationStatus.Requested"/> succeeds; anything
    /// else fails with <see cref="LeaseLostException"/>.
    /// </summary>
    ValueTask<WorkflowExternalOperation> AcquireAsync(
        Guid operationId,
        string ownerId,
        long leaseGeneration,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a result for a handle owned by the caller. Only the acquiring
    /// owner running the recorded attempt may complete it; anything else fails
    /// with <see cref="LeaseLostException"/>. A result recorded after the run
    /// moved generation stays visible with its original generation so readers
    /// can refuse to advance obsolete control flow.
    /// </summary>
    ValueTask<WorkflowExternalOperation> CompleteAsync(
        Guid operationId,
        string ownerId,
        string? payloadJson,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a failure for a handle owned by the caller, fenced like
    /// <see cref="CompleteAsync"/>.
    /// </summary>
    ValueTask<WorkflowExternalOperation> FailAsync(
        Guid operationId,
        string ownerId,
        string? error,
        CancellationToken cancellationToken = default);
}
