using Microsoft.Extensions.Logging;

namespace Penghou.Zhinu;

/// <summary>Stable structured-log event identifiers for durable lifecycle transitions.</summary>
public static class ZhinuLogEvents
{
    public static readonly EventId RunCreated = new(1001, "WorkflowRunCreated");
    public static readonly EventId RunExecuting = new(1002, "WorkflowRunExecuting");
    public static readonly EventId RunCompleted = new(1003, "WorkflowRunCompleted");
    public static readonly EventId RunFailed = new(1004, "WorkflowRunFailed");
    public static readonly EventId RunCancelled = new(1005, "WorkflowRunCancelled");
    public static readonly EventId StepRestarted = new(1006, "StepRestarted");
    public static readonly EventId SignalBuffered = new(1007, "SignalBuffered");
    public static readonly EventId RunCompensated = new(1008, "WorkflowRunCompensated");
    public static readonly EventId RunRolledBackAndRestarted = new(1009, "WorkflowRunRolledBackAndRestarted");
}
