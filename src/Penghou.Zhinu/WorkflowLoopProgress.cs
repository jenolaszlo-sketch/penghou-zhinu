namespace Penghou.Zhinu;

/// <summary>Point-in-time durable state of one loop iteration.</summary>
public sealed record WorkflowLoopIterationProgress
{
    public required WorkflowLoopIterationReference Iteration { get; init; }

    public WorkflowStepRun? ConditionStep { get; init; }

    public IReadOnlyList<WorkflowStepRun> BodySteps { get; init; } = [];

    public WorkflowStepRun? CommitStep { get; init; }

    /// <summary>The committed lexical control outcome, when available.</summary>
    public LoopBodyOutcomeKind? Outcome { get; init; }

    public bool WasEntered => BodySteps.Count != 0 || CommitStep is not null;

    public bool IsCommitted => CommitStep?.Status == StepStatus.Completed;

    /// <summary>The first failure at the condition, body, or commit boundary.</summary>
    public WorkflowError? Error =>
        ConditionStep?.Error ??
        BodySteps.FirstOrDefault(step => step.Error is not null)?.Error ??
        CommitStep?.Error;
}

/// <summary>
/// Point-in-time durable state of a root or nested loop, grouped by semantic
/// boundaries rather than provider storage keys.
/// </summary>
public sealed record WorkflowLoopProgress
{
    public required Guid WorkflowRunId { get; init; }

    public required WorkflowLoopReference Loop { get; init; }

    public IReadOnlyList<WorkflowLoopIterationProgress> Iterations { get; init; } = [];

    public WorkflowStepRun? LimitStep { get; init; }

    public WorkflowStepRun? FinalStep { get; init; }

    public WorkflowLoopIterationProgress? CurrentIteration =>
        Iterations.Count == 0 ? null : Iterations[^1];

    public bool HasStarted =>
        Iterations.Count != 0 || LimitStep is not null || FinalStep is not null;

    public bool IsCompleted => FinalStep?.Status == StepStatus.Completed;
}
