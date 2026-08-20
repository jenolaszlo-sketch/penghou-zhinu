namespace Penghou.Zhinu;

/// <summary>
/// Persists current workflow state and transactional diagnostic events.
/// Implementations are responsible for atomic claims and state transitions.
/// </summary>
public interface IWorkflowStore :
    IWorkflowRepository,
    IWorkflowStepRepository,
    IWorkflowSignalRepository,
    IWorkflowTimerRepository,
    IWorkflowLeaseRepository
{
}
