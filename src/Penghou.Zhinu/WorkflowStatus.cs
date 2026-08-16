namespace Penghou.Zhinu;

/// <summary>Describes the durable lifecycle state of a workflow run.</summary>
public enum WorkflowStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    Cancelled
}
