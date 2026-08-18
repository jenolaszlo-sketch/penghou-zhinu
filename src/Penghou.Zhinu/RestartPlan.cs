namespace Penghou.Zhinu;

/// <summary>Why a step is included in a restart plan.</summary>
public enum RestartReason
{
    /// <summary>The step the restart targets directly.</summary>
    Requested,

    /// <summary>A transitive dependent of the requested step.</summary>
    Dependent,

    /// <summary>Invalidated because it was created at or after the requested step.</summary>
    CreationOrderFallback
}

/// <summary>One step affected by a restart, and why.</summary>
public sealed record RestartPlanStep(
    string StepKey,
    RestartReason Reason);

/// <summary>
/// The set of steps a restart would invalidate, resolved before any state is
/// changed. <c>PlanRestartAsync</c> returns this for inspection and
/// confirmation; <c>RestartStepAsync</c> applies it atomically.
/// </summary>
public sealed record RestartPlan(
    Guid WorkflowRunId,
    string TargetStepKey,
    IReadOnlyList<RestartPlanStep> StepsToInvalidate);
