namespace Penghou.Zhinu;

internal static class ActivityGroupRuntime
{
    internal static ActivityGroupManifest? Capture(IWorkflowRegistration registration, IWorkflowStore store)
    {
        var manifest = (registration as IWorkflowGroupManifestProvider)?.GroupManifest;
        if (manifest is not null && store is not IWorkflowGroupRepository)
            throw new WorkflowConfigurationException("Grouped workflows require a durable IWorkflowGroupRepository.");
        return manifest;
    }

    internal static ValueTask CreateRunAsync(IWorkflowStore store, WorkflowRun run,
        ActivityGroupManifest? manifest, CancellationToken cancellationToken) =>
        manifest is null ? store.CreateRunAsync(run, cancellationToken) :
            ((IWorkflowGroupRepository)store).CreateGroupedRunAsync(run, manifest, cancellationToken);

    internal static async ValueTask ValidateRunAsync(IWorkflowStore store, WorkflowRun run,
        ActivityGroupManifest? manifest, CancellationToken cancellationToken)
    {
        if (run.GroupFanOutUsed && store is not IWorkflowGroupFanOutRepository)
            throw new WorkflowConfigurationException("The run requires the durable grouped fan-out repository capability.");
        if (!string.Equals(run.GroupManifestDigest, manifest?.ManifestDigest, StringComparison.Ordinal))
            throw new WorkflowConfigurationException("The registered group manifest does not match the admitted run.");
        if (run.GroupManifestDigest is null)
            return;
        if (store is not IWorkflowGroupRepository groups)
            throw new WorkflowConfigurationException("The grouped run requires a durable IWorkflowGroupRepository.");
        var persisted = await groups.GetGroupManifestAsync(run.Id, cancellationToken).ConfigureAwait(false);
        if (persisted is null || persisted.ManifestDigest != run.GroupManifestDigest ||
            persisted.CanonicalJson != manifest!.CanonicalJson)
            throw new WorkflowConfigurationException("The run's durable group manifest is missing or inconsistent.");
    }

    internal static void ValidateBinding(Guid runId, ActivityGroupManifest? manifest, ActivityGroupStepBinding? binding,
        DurableLoopIterationIdentity? iteration = null, string? fanOutCoordinate = null)
    {
        if (binding is null)
            return;
        if (manifest is null || binding.Occurrence.WorkflowRunId != runId ||
            binding.Occurrence.ManifestDigest != manifest.ManifestDigest)
            throw new WorkflowConfigurationException("Step grouping must use an occurrence from this run's admitted manifest.");
        manifest.ValidateMembership(binding.Membership);
        var groupFrames = ActivityGroupOccurrence.ReadCoordinate(binding.Occurrence.ExecutionCoordinate);
        if (iteration is not null && fanOutCoordinate is not null)
            throw new WorkflowConfigurationException("Nested grouped fan-out scopes are not qualified by this profile.");
        var executionFrames = ActivityGroupOccurrence.ReadCoordinate(fanOutCoordinate ?? ActivityGroupOccurrence.Coordinate(iteration));
        if (groupFrames.Count > executionFrames.Count || !groupFrames.SequenceEqual(executionFrames.Take(groupFrames.Count)))
            throw new WorkflowConfigurationException("The group occurrence must belong to this step's typed execution scope or an enclosing scope.");
    }
}
