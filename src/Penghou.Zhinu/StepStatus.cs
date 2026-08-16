namespace Penghou.Zhinu;

/// <summary>Describes the durable lifecycle state of a workflow step.</summary>
public enum StepStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    Waiting,
    Cancelled
}
