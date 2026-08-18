namespace Penghou.Zhinu;

/// <summary>
/// A point-in-time snapshot of one run's progress: the run itself, its durable
/// steps, its recent diagnostic events, and (recursively) the progress of any
/// child runs started via <c>StartChildAsync</c>.
/// </summary>
public sealed record WorkflowRunProgress
{
    public required WorkflowRun Run { get; init; }

    public IReadOnlyList<WorkflowStepRun> Steps { get; init; } = [];

    public IReadOnlyList<WorkflowEvent> Events { get; init; } = [];

    public IReadOnlyList<WorkflowRunProgress> Children { get; init; } = [];

    /// <summary>Distinct keys of steps that reached a terminal state.</summary>
    public IReadOnlyList<string> ExecutedStepKeys =>
        Steps.Where(step => step.Status is StepStatus.Completed or StepStatus.Failed)
            .Select(step => step.StepKey)
            .Distinct()
            .ToArray();

    public int CompletedSteps => Steps.Count(step => step.Status == StepStatus.Completed);

    public int WaitingSteps => Steps.Count(step => step.Status == StepStatus.Waiting);

    public int FailedSteps => Steps.Count(step => step.Status == StepStatus.Failed);

    public int RunningSteps => Steps.Count(step => step.Status == StepStatus.Running);
}
