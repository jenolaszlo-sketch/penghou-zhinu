namespace Penghou.Zhinu;

public sealed class WorkflowExecutionFailedException(
    Guid workflowRunId,
    WorkflowError error)
    : ZhinuException($"Workflow '{workflowRunId:D}' failed: {error.Message}")
{
    public Guid WorkflowRunId { get; } = workflowRunId;

    public WorkflowError Error { get; } = error;
}
