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
        var index = entry.StateIndex;
        if (!index.Occurrences.Contains(occurrenceId)) return new(WorkflowGroupReadStatus.OccurrenceNotFound);
        var subtree = new HashSet<string>(StringComparer.Ordinal) { occurrenceId };
        var pending = new Queue<string>();
        pending.Enqueue(occurrenceId);
        while (pending.TryDequeue(out var parent))
            foreach (var child in index.Children[parent]) if (subtree.Add(child.OccurrenceId)) pending.Enqueue(child.OccurrenceId);
        var members = new Dictionary<string, ActivityGroupMemberRole>(StringComparer.Ordinal);
        var selectionConsistent = true;
        var currentExpansions = new HashSet<WorkflowGroupExpansionFact>();
        foreach (var id in subtree)
        {
            foreach (var step in index.Members[id]) members.Add(step.StepKey, step.Role!.Value);
            foreach (var item in index.Items[id])
            {
                if (index.Facts.Current.TryGetValue(item.StepKey, out var step)
                    && (step.GroupOccurrenceId != item.OccurrenceId || step.Role != item.Role)) selectionConsistent = false;
                if (!members.TryAdd(item.StepKey, item.Role) && members[item.StepKey] != item.Role) selectionConsistent = false;
            }
            foreach (var expansion in index.Expansions[id]) currentExpansions.Add(expansion);
        }

        var isItem = index.Items[occurrenceId].Count() == 1 && subtree.Count == 1 && members.Count == 1;
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
            Coverage = !selectionConsistent || index.Historical.Contains(occurrenceId) ? WorkflowGroupCoverage.Unknown :
                subtree.Any(index.Historical.Contains) ? WorkflowGroupCoverage.Partial : WorkflowGroupCoverage.Complete
        };
        var state = WorkflowGroupStateReducer.Evaluate(input, entry.Header.CapturedAt, index.Facts);
        if (index.Historical.Contains(occurrenceId)) state = state with
        {
            Reason = WorkflowGroupStateReason.HistoricalSelection,
            Reasons = Copy(state.Reasons.Append(WorkflowGroupStateReason.HistoricalSelection).Order())
        };
        return new(WorkflowGroupReadStatus.Success, new(snapshotToken, data.ThroughDurableSequence, entry.Header.CapturedAt, occurrenceId, state));
    }

    private sealed class StateIndex
    {
        public required WorkflowGroupStateReducer.Facts Facts { get; init; }
        public required HashSet<string> Occurrences { get; init; }
        public required HashSet<string> Historical { get; init; }
        public required ILookup<string, WorkflowGroupOccurrenceFact> Children { get; init; }
        public required ILookup<string, WorkflowGroupStepFact> Members { get; init; }
        public required ILookup<string, WorkflowGroupFanOutItemFact> Items { get; init; }
        public required ILookup<string, WorkflowGroupExpansionFact> Expansions { get; init; }

        // Charge bucket arrays, lookup group objects and retained references as well
        // as frozen DTOs. No query results or subtree copies are retained per group.
        public static long EstimateBytes(WorkflowGroupSnapshotData data) => 4096L
            + data.Occurrences.Count * 320L + data.Steps.Count * 512L
            + (data.Dependencies.Count + (long)data.Waits.Count + data.Approvals.Count) * 192L
            + data.Expansions.Sum(expansion => 128L + expansion.Items.Count * 256L);

        public static StateIndex? Create(WorkflowGroupSnapshotData data)
        {
            var facts = WorkflowGroupStateReducer.Prepare(new WorkflowGroupStateInput
            {
                Run = data.Run,
                Members = [],
                Steps = data.Steps,
                Dependencies = data.Dependencies,
                Waits = data.Waits,
                Approvals = data.Approvals
            });
            if (facts is null) return null;
            var activeItems = data.Expansions.Where(expansion => expansion.IsCurrent).SelectMany(expansion => expansion.Items).ToArray();
            var active = activeItems.Select(item => item.OccurrenceId).ToHashSet(StringComparer.Ordinal);
            var historical = data.Expansions.Where(expansion => !expansion.IsCurrent).SelectMany(expansion => expansion.Items)
                .Select(item => item.OccurrenceId).Where(id => !active.Contains(id)).ToHashSet(StringComparer.Ordinal);
            return new StateIndex
            {
                Facts = facts,
                Occurrences = data.Occurrences.Select(occurrence => occurrence.OccurrenceId).ToHashSet(StringComparer.Ordinal),
                Historical = historical,
                Children = data.Occurrences.ToLookup(occurrence => occurrence.ParentOccurrenceId ?? "", StringComparer.Ordinal),
                Members = facts.Current.Values.Where(step => step.GroupOccurrenceId is { } id && !historical.Contains(id))
                    .ToLookup(step => step.GroupOccurrenceId!, StringComparer.Ordinal),
                Items = activeItems.ToLookup(item => item.OccurrenceId, StringComparer.Ordinal),
                Expansions = data.Expansions.Where(expansion => expansion.IsCurrent)
                    .SelectMany(expansion => expansion.Items.Select(item => item.OccurrenceId).Distinct(StringComparer.Ordinal)
                        .Select(id => (id, expansion))).ToLookup(pair => pair.id, pair => pair.expansion, StringComparer.Ordinal)
            };
        }
    }
}
