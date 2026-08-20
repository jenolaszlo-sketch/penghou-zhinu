namespace Penghou.Zhinu;

/// <summary>Persists atomic workflow fork previews and applications.</summary>
public interface IWorkflowForkRepository
{
    ValueTask<ForkPlan> PlanForkAsync(
        Guid sourceWorkflowRunId,
        string targetStepKey,
        StepRestartMode mode,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically creates <paramref name="newRun"/> and copies reusable,
    /// completed source steps into it. The source run is never changed.
    /// </summary>
    ValueTask<ForkPlan> ForkRunAsync(
        Guid sourceWorkflowRunId,
        WorkflowRun newRun,
        string targetStepKey,
        StepRestartMode mode,
        string? actor,
        string? reason,
        CancellationToken cancellationToken = default);
}
