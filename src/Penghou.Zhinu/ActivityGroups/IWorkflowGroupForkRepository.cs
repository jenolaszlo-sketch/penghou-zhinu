namespace Penghou.Zhinu;

/// <summary>
/// Optional atomic group-aware fork capability. Copied memberships and their
/// ancestors must be rebound to the destination run without changing native
/// fork selection, step provenance, execution coordinates or source state.
/// </summary>
public interface IWorkflowGroupForkRepository
{
    /// <summary>
    /// Creates the pending destination, its optional manifest, reusable steps,
    /// destination-owned occurrence lineage, dependencies and fork event in one
    /// transaction. A missing or incompatible target for a reused membership
    /// must reject the entire fork. Ungrouped copied steps remain ungrouped.
    /// </summary>
    ValueTask<ForkPlan> ForkGroupedRunAsync(
        Guid sourceWorkflowRunId,
        WorkflowRun newRun,
        ActivityGroupManifest? targetManifest,
        string targetStepKey,
        StepRestartMode mode,
        string? actor,
        string? reason,
        CancellationToken cancellationToken = default);
}
