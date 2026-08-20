namespace Penghou.Zhinu;

/// <summary>A non-throwing point-in-time view of a workflow outcome.</summary>
public sealed record WorkflowResult<T>
{
    public required Guid WorkflowRunId { get; init; }
    public required WorkflowStatus Status { get; init; }
    public T? Value { get; init; }
    public WorkflowError? Error { get; init; }
    public bool IsTerminal => Status is WorkflowStatus.Completed or WorkflowStatus.Failed or
        WorkflowStatus.Cancelled or WorkflowStatus.Compensated;
}
