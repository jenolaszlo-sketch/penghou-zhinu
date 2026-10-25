namespace Penghou.Zhinu;

/// <summary>Optional registration capability; existing registrations need not implement it.</summary>
public interface IWorkflowGroupManifestProvider
{
    ActivityGroupManifest? GroupManifest { get; }
}

/// <summary>
/// Optional durable group capability. These are bounded persistence point reads,
/// not a hierarchy/progress projection. Grouped run creation must be atomic with
/// manifest installation and the existing run-start event. Claims must validate
/// pins and persist membership atomically with their existing step transition.
/// </summary>
public interface IWorkflowGroupRepository
{
    ValueTask CreateGroupedRunAsync(WorkflowRun run, ActivityGroupManifest manifest, CancellationToken cancellationToken = default);
    ValueTask<ActivityGroupManifest?> GetGroupManifestAsync(Guid workflowRunId, CancellationToken cancellationToken = default);
    ValueTask<ActivityGroupOccurrence?> GetGroupOccurrenceAsync(Guid workflowRunId, string groupOccurrenceId, CancellationToken cancellationToken = default);
    ValueTask<ActivityGroupStepMembership?> GetStepGroupMembershipAsync(Guid stepId, CancellationToken cancellationToken = default);
}
