namespace Penghou.Zhinu;

/// <summary>Where a rollback-to-step boundary falls relative to the target step.</summary>
public enum RollbackBoundary
{
    /// <summary>
    /// The boundary sits before the target step: the target and everything that
    /// (transitively) depends on it are compensated.
    /// </summary>
    BeforeStep,

    /// <summary>
    /// The boundary sits after the target step: the target itself is preserved
    /// and only its (transitive) dependents are compensated.
    /// </summary>
    AfterStep
}

/// <summary>What a rollback does with a step.</summary>
public enum RollbackAction
{
    /// <summary>The step's committed forward operation is undone.</summary>
    Compensate,

    /// <summary>The step's committed forward operation is left intact.</summary>
    Preserve
}

/// <summary>Why a step is included in a rollback plan.</summary>
public enum RollbackReason
{
    /// <summary>The step the rollback targets directly (its boundary).</summary>
    Boundary,

    /// <summary>A step that (transitively) depends on the target.</summary>
    Dependent,

    /// <summary>A step in a branch unrelated to the target.</summary>
    IndependentBranch,

    /// <summary>A step the target itself depends on.</summary>
    Ancestor
}

/// <summary>Options for planning or executing a rollback to a specific step.</summary>
public sealed record RollbackOptions(RollbackBoundary Boundary);

/// <summary>One step affected by a rollback, what happens to it, and why.</summary>
public sealed record RollbackPlanStep(
    string StepKey,
    RollbackAction Action,
    RollbackReason Reason);

/// <summary>
/// The set of steps a rollback would compensate, resolved before any state is
/// changed. Steps are listed in execution order: compensated steps first, in
/// reverse dependency order, followed by preserved steps in creation order.
/// <c>PlanRollbackAsync</c> returns this for inspection and confirmation;
/// <c>RollbackAsync</c> and <c>RollbackToStepAsync</c> apply it.
/// </summary>
public sealed record RollbackPlan(
    Guid WorkflowRunId,
    string? TargetStepKey,
    RollbackBoundary Boundary,
    IReadOnlyList<RollbackPlanStep> Steps)
{
    /// <summary>The steps that would be compensated, in execution order.</summary>
    public IReadOnlyList<RollbackPlanStep> CompensatedSteps => Steps
        .Where(step => step.Action == RollbackAction.Compensate)
        .ToArray();
}
