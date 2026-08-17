namespace Penghou.Zhinu;

/// <summary>
/// Thrown when a registered <see cref="IWorkflowEventPublisher"/> fails to
/// forward an event after the event was durably committed to the store. The
/// event remains committed; only the best-effort notification failed.
/// </summary>
public sealed class WorkflowEventPublisherException : Exception
{
    public WorkflowEventPublisherException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
