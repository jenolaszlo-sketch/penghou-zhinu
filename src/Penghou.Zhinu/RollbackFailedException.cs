namespace Penghou.Zhinu;

/// <summary>
/// Thrown when a rollback could not compensate every planned step, so the run
/// could not reach the <see cref="WorkflowStatus.Compensated"/> terminal state.
/// Compensations already completed before the failure are reused by a later
/// rollback attempt (at-least-once execution).
/// </summary>
public sealed class RollbackFailedException : ZhinuException
{
    public RollbackFailedException(string message)
        : base(message)
    {
    }

    public RollbackFailedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
