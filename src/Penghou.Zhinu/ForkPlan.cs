namespace Penghou.Zhinu;

/// <summary>Why a source step will execute again in a forked run.</summary>
public enum ForkStepReason
{
    Requested,
    Dependent,
    CreationOrderFallback,
    NotCompleted
}

/// <summary>One source step that will not be reused by a fork.</summary>
public sealed record ForkPlanStep(
    string StepKey,
    ForkStepReason Reason);

/// <summary>
/// Preview of the committed results a new run will reuse and the steps it will
/// execute again. Applying a fork never mutates the source run.
/// </summary>
public sealed record ForkPlan(
    Guid SourceWorkflowRunId,
    string TargetStepKey,
    StepRestartMode Mode,
    IReadOnlyList<string> StepsToReuse,
    IReadOnlyList<ForkPlanStep> StepsToReexecute);
