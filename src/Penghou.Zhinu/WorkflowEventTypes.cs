namespace Penghou.Zhinu;

/// <summary>Provides stable identifiers for built-in workflow execution events.</summary>
public static class WorkflowEventTypes
{
    public const string WorkflowStarted = "workflow-started";
    public const string WorkflowResumed = "workflow-resumed";
    public const string WorkflowCompleted = "workflow-completed";
    public const string WorkflowFailed = "workflow-failed";
    public const string WorkflowCancelled = "workflow-cancelled";
    public const string StepStarted = "step-started";
    public const string StepReused = "step-reused";
    public const string StepCompleted = "step-completed";
    public const string StepFailed = "step-failed";
    public const string RetryScheduled = "retry-scheduled";
    public const string DelayScheduled = "delay-scheduled";
    public const string LeaseRecovered = "lease-recovered";
}
