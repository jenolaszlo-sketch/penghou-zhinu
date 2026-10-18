namespace Penghou.Zhinu;

public sealed partial class WorkflowGroupReader
{
    public async ValueTask<WorkflowGroupQueryResult<WorkflowGroupStateSnapshot>> GetGroupStateAsync(Guid workflowRunId,
        string snapshotToken, string occurrenceId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(occurrenceId);
        var found = await FindAsync(workflowRunId, snapshotToken, cancellationToken);
        if (found.Value is null) return new(found.Status);
        var entry = found.Value;
        var data = entry.Data;
        if (!data.Occurrences.Any(occurrence => occurrence.OccurrenceId == occurrenceId)) return new(WorkflowGroupReadStatus.OccurrenceNotFound);
        var children = data.Occurrences.ToLookup(occurrence => occurrence.ParentOccurrenceId ?? "", StringComparer.Ordinal);
        var subtree = new HashSet<string>(StringComparer.Ordinal) { occurrenceId };
        var pending = new Queue<string>();
        pending.Enqueue(occurrenceId);
        while (pending.TryDequeue(out var parent))
            foreach (var child in children[parent]) if (subtree.Add(child.OccurrenceId)) pending.Enqueue(child.OccurrenceId);

        var activeItems = data.Expansions.Where(expansion => expansion.IsCurrent).SelectMany(expansion => expansion.Items).ToArray();
        var activeOccurrences = activeItems.Select(item => item.OccurrenceId).ToHashSet(StringComparer.Ordinal);
        var historical = data.Expansions.Where(expansion => !expansion.IsCurrent).SelectMany(expansion => expansion.Items)
            .Select(item => item.OccurrenceId).Where(id => !activeOccurrences.Contains(id)).ToHashSet(StringComparer.Ordinal);
        var members = new Dictionary<string, ActivityGroupMemberRole>(StringComparer.Ordinal);
        var selectionConsistent = true;
        var currentSteps = data.Steps.Where(step => step.IsCurrent).ToDictionary(step => step.StepKey, StringComparer.Ordinal);
        foreach (var step in data.Steps.Where(step => step.IsCurrent && step.GroupOccurrenceId is { } id && subtree.Contains(id) && !historical.Contains(id)))
            members.Add(step.StepKey, step.Role!.Value);
        foreach (var item in activeItems.Where(item => subtree.Contains(item.OccurrenceId)))
        {
            if (currentSteps.TryGetValue(item.StepKey, out var step)
                && (step.GroupOccurrenceId != item.OccurrenceId || step.Role != item.Role)) selectionConsistent = false;
            if (!members.TryAdd(item.StepKey, item.Role) && members[item.StepKey] != item.Role) selectionConsistent = false;
        }

        var isItem = activeItems.Count(item => item.OccurrenceId == occurrenceId) == 1 && subtree.Count == 1 && members.Count == 1;
        var currentExpansions = data.Expansions.Where(expansion => expansion.IsCurrent && expansion.Items.Any(item => subtree.Contains(item.OccurrenceId))).ToArray();
        var closed = isItem || data.Run.Status == WorkflowStatus.Completed && currentExpansions.All(expansion => expansion.IsComplete);
        var extent = closed ? WorkflowGroupExtent.Closed : data.Run.Status is WorkflowStatus.Cancelled or WorkflowStatus.Failed
            || data.Generation?.Status == WorkflowGenerationStatus.Superseded ? WorkflowGroupExtent.Interrupted : WorkflowGroupExtent.Open;
        var input = new WorkflowGroupStateInput
        {
            Run = data.Run,
            Members = members.Select(member => new WorkflowGroupSelectedMember(member.Key, member.Value)).ToArray(),
            Steps = data.Steps,
            Dependencies = data.Dependencies,
            Waits = data.Waits,
            Approvals = data.Approvals,
            Generation = data.Generation,
            Operation = data.Operation,
            Extent = extent,
            Coverage = !selectionConsistent || historical.Contains(occurrenceId) ? WorkflowGroupCoverage.Unknown :
                historical.Any(subtree.Contains) ? WorkflowGroupCoverage.Partial : WorkflowGroupCoverage.Complete
        };
        var state = WorkflowGroupStateReducer.Evaluate(input, entry.Header.CapturedAt);
        if (historical.Contains(occurrenceId)) state = state with
        {
            Reason = WorkflowGroupStateReason.HistoricalSelection,
            Reasons = Copy(state.Reasons.Append(WorkflowGroupStateReason.HistoricalSelection).Order())
        };
        return new(WorkflowGroupReadStatus.Success, new(snapshotToken, data.ThroughDurableSequence, entry.Header.CapturedAt, occurrenceId, state));
    }
}
