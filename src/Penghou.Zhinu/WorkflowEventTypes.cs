using System.Collections.Frozen;

namespace Penghou.Zhinu;

/// <summary>
/// Provides stable identifiers for built-in workflow execution events and
/// classifies an event type by its durability authority.
/// </summary>
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
    public const string Progress = "progress";
    public const string StepRestarted = "step-restarted";
    public const string RunForked = "run-forked";
    public const string SignalSent = "signal-sent";
    public const string SignalDelivered = "signal-delivered";
    public const string CompensationStarted = "compensation-started";
    public const string CompensationCompleted = "compensation-completed";
    public const string CompensationFailed = "compensation-failed";
    public const string WorkflowCompensated = "workflow-compensated";
    public const string WorkflowRestarted = "workflow-restarted";
    public const string ArtifactPublished = "artifact-published";
    public const string ArtifactInvalidated = "artifact-invalidated";
    public const string StepCompletionRefused = "step-completion-refused";
    public const string LoopIterationCommitted = "loop-iteration-committed";
    public const string LoopCompleted = "loop-completed";
    public const string LoopLimitExceeded = "loop-limit-exceeded";

    /// <summary>
    /// Classifies a workflow event type by durability authority.
    /// <see cref="WorkflowEventDurability.Advisory"/> is a closed, versioned set of
    /// informational types (currently only <see cref="Progress"/>); every other
    /// type, including application-defined types emitted through
    /// <c>EmitAsync</c>, is <see cref="WorkflowEventDurability.Durable"/> because
    /// emitted events are committed-transition evidence. The classification is a
    /// pure function of the persisted event type, so it is stable across store
    /// reopen and does not alter event ordering or cursor/export semantics.
    /// </summary>
    public static WorkflowEventDurability Durability(string eventType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);
        return AdvisoryTypes.Contains(eventType)
            ? WorkflowEventDurability.Advisory
            : WorkflowEventDurability.Durable;
    }

    private static readonly FrozenSet<string> AdvisoryTypes =
        new[] { Progress }.ToFrozenSet(StringComparer.Ordinal);
}
