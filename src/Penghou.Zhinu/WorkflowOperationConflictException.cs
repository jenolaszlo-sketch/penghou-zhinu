namespace Penghou.Zhinu;

/// <summary>
/// Thrown when a durable operation identity is reused for different intent.
/// </summary>
public sealed class WorkflowOperationConflictException : WorkflowStateException
{
    public WorkflowOperationConflictException(Guid operationId, string message)
        : base(message)
    {
        OperationId = operationId;
    }

    public Guid OperationId { get; }
}
