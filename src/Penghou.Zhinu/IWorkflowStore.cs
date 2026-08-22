namespace Penghou.Zhinu;

/// <summary>
/// Persists current workflow state and transactional diagnostic events.
/// Implementations are responsible for atomic claims and state transitions.
/// Every method documented as atomic must commit all durable state and events
/// in one transaction; returning false or null must not leave partial changes.
/// Implementations should run the Penghou.Zhinu.Testing conformance checks.
/// </summary>
public interface IWorkflowStore :
    IWorkflowRepository,
    IWorkflowStepRepository,
    IWorkflowSignalRepository,
    IWorkflowTimerRepository,
    IWorkflowLeaseRepository,
    IWorkflowForkRepository,
    IWorkflowArtifactRepository
{
    /// <summary>
    /// Performs a safe health probe: verifies the backing store can be opened,
    /// its schema is compatible, and a trivial read succeeds. Readiness
    /// endpoints use this; it must never claim a production step or mutate state.
    /// </summary>
    ValueTask<WorkflowStoreHealth> CheckHealthAsync(
        CancellationToken cancellationToken = default);
}
