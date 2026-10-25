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
        var isSealedBodySubtree = index.SealedBodyOccurrenceIds.Contains(occurrenceId);
        var closed = isItem || data.Run.Status == WorkflowStatus.Completed && currentExpansions.All(expansion => expansion.IsComplete);
        closed |= isSealedBodySubtree;
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
                subtree.Any(index.UnresolvedHistorical.Contains) ? WorkflowGroupCoverage.Partial : WorkflowGroupCoverage.Complete
        };
        var state = WorkflowGroupStateReducer.Evaluate(input, entry.Header.CapturedAt, index.Facts);
        if (index.Historical.Contains(occurrenceId)) state = state with
        {
            Reason = WorkflowGroupStateReason.HistoricalSelection,
            Reasons = Copy(state.Reasons.Append(WorkflowGroupStateReason.HistoricalSelection).Order())
        };
        return new(WorkflowGroupReadStatus.Success, new(snapshotToken, data.ThroughDurableSequence, entry.Header.CapturedAt, occurrenceId, state));
    }

    private static HashSet<string> FindSealedBodyOccurrences(
        IReadOnlyList<WorkflowGroupExpansionFact> expansions,
        IReadOnlyDictionary<string, WorkflowGroupOccurrenceFact> occurrences,
        ILookup<string, WorkflowGroupOccurrenceFact> children,
        ILookup<string, WorkflowGroupStepFact> members,
        WorkflowGroupStateReducer.Facts facts,
        IReadOnlyList<StepDependency> dependencies)
    {
        var sealedOccurrences = new HashSet<string>(StringComparer.Ordinal);
        var currentByKey = facts.Current;
        var currentById = currentByKey.Values.ToDictionary(step => step.StepId);
        var dependenciesByKey = dependencies.GroupBy(edge => edge.StepKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(edge => edge.DependsOnStepKey)
                .ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
        foreach (var expansion in expansions.Where(candidate => candidate.IsCurrent && candidate.BodyContract is not null))
        {
            if (!currentById.TryGetValue(expansion.BoundaryStepId, out var boundary)
                || boundary.Revision != expansion.BoundaryRevision || boundary.Status != StepStatus.Completed) continue;
            var required = expansion.RequiredDependencies.ToHashSet(StringComparer.Ordinal);
            if (required.Count == 0 || !required.Contains(boundary.StepKey)
                || required.Any(key => !currentByKey.TryGetValue(key, out var dependency) || dependency.Status != StepStatus.Completed)) continue;
            foreach (var item in expansion.Items)
            {
                if (item.Role != ActivityGroupMemberRole.Control || !ValidInputHash(item.InputHash)
                    || !occurrences.TryGetValue(item.OccurrenceId, out var itemRoot)
                    || !currentByKey.TryGetValue(item.StepKey, out var completion)
                    || !completion.IsCurrent || completion.Status != StepStatus.Completed
                    || completion.GroupOccurrenceId != item.OccurrenceId || completion.Role != ActivityGroupMemberRole.Control
                    || !ValidInputHash(completion.InputHash)
                    || !string.Equals(completion.InputHash, item.InputHash, StringComparison.Ordinal)) continue;

                if (!dependenciesByKey.TryGetValue(item.StepKey, out var completionDependencies)
                    || !required.IsSubsetOf(completionDependencies)) continue;

                var descendantIds = new List<string>();
                var pending = new Queue<string>();
                var itemMembers = new List<WorkflowGroupStepFact>();
                var coordinateIsConsistent = true;
                pending.Enqueue(item.OccurrenceId);
                while (pending.TryDequeue(out var occurrenceId))
                {
                    if (!occurrences.TryGetValue(occurrenceId, out var occurrence)
                        || occurrence.ExecutionCoordinate != itemRoot.ExecutionCoordinate)
                    {
                        coordinateIsConsistent = false;
                        break;
                    }
                    descendantIds.Add(occurrenceId);
                    itemMembers.AddRange(members[occurrenceId]);
                    foreach (var child in children[occurrenceId]) pending.Enqueue(child.OccurrenceId);
                }
                if (!coordinateIsConsistent || descendantIds.Count == 0 || !itemMembers.Any(member => member.StepKey == item.StepKey
                        && member.Status == StepStatus.Completed && member.Role == ActivityGroupMemberRole.Control)) continue;
                var bodyMembers = itemMembers.Where(member => member.StepKey != item.StepKey).ToArray();
                if (bodyMembers.Length == 0 || bodyMembers.Any(member => member.Role != ActivityGroupMemberRole.Work
                        || member.Status != StepStatus.Completed)) continue;
                var bodyKeys = bodyMembers.Select(member => member.StepKey).ToHashSet(StringComparer.Ordinal);
                if (!bodyKeys.SetEquals(completionDependencies.Except(required, StringComparer.Ordinal))) continue;
                sealedOccurrences.UnionWith(descendantIds);
            }
        }
        return sealedOccurrences;
    }

    private static bool ValidInputHash(string? value) => value is { Length: 64 }
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private sealed class StateIndex
    {
        public required WorkflowGroupStateReducer.Facts Facts { get; init; }
        public required HashSet<string> Occurrences { get; init; }
        public required HashSet<string> SealedBodyOccurrenceIds { get; init; }
        public required HashSet<string> Historical { get; init; }
        public required HashSet<string> UnresolvedHistorical { get; init; }
        public required ILookup<string, WorkflowGroupOccurrenceFact> Children { get; init; }
        public required ILookup<string, WorkflowGroupStepFact> Members { get; init; }
        public required ILookup<string, WorkflowGroupFanOutItemFact> Items { get; init; }
        public required ILookup<string, WorkflowGroupExpansionFact> Expansions { get; init; }

        // Charge bucket arrays, lookup group objects and retained references as well
        // as frozen DTOs. No query results or subtree copies are retained per group.
        public static long EstimateBytes(WorkflowGroupSnapshotData data) => 4096L
            + data.Occurrences.Count * 448L + data.Steps.Count * 576L
            + (data.Dependencies.Count + (long)data.Waits.Count + data.Approvals.Count) * 192L
            + data.Expansions.Sum(expansion => 256L + expansion.Items.Count * 320L);

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
            var occurrencesById = data.Occurrences.ToDictionary(occurrence => occurrence.OccurrenceId, StringComparer.Ordinal);
            var active = activeItems.Select(item => item.OccurrenceId).ToHashSet(StringComparer.Ordinal);
            var historical = data.Expansions.Where(expansion => !expansion.IsCurrent).SelectMany(expansion => expansion.Items)
                .Select(item => item.OccurrenceId).Where(id => !active.Contains(id)).ToHashSet(StringComparer.Ordinal);
            var children = data.Occurrences.ToLookup(occurrence => occurrence.ParentOccurrenceId ?? "", StringComparer.Ordinal);
            var retired = data.Expansions.Where(expansion => expansion.ReplacedByExpansionId is not null)
                .SelectMany(expansion => expansion.Items).Select(item => item.OccurrenceId).ToHashSet(StringComparer.Ordinal);
            ExpandDescendants(historical, children);
            ExpandDescendants(retired, children);
            historical.UnionWith(retired);
            var members = facts.Current.Values.Where(step => step.GroupOccurrenceId is { } id && !historical.Contains(id))
                .ToLookup(step => step.GroupOccurrenceId!, StringComparer.Ordinal);
            var currentExpansions = data.Expansions.Where(expansion => expansion.IsCurrent).ToArray();
            return new StateIndex
            {
                Facts = facts,
                Occurrences = data.Occurrences.Select(occurrence => occurrence.OccurrenceId).ToHashSet(StringComparer.Ordinal),
                SealedBodyOccurrenceIds = FindSealedBodyOccurrences(currentExpansions, occurrencesById, children,
                    members, facts, data.Dependencies),
                Historical = historical,
                UnresolvedHistorical = historical.Except(retired, StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal),
                Children = children,
                Members = members,
                Items = activeItems.ToLookup(item => item.OccurrenceId, StringComparer.Ordinal),
                Expansions = currentExpansions
                    .SelectMany(expansion => expansion.Items.Select(item => item.OccurrenceId).Distinct(StringComparer.Ordinal)
                        .Select(id => (id, expansion))).ToLookup(pair => pair.id, pair => pair.expansion, StringComparer.Ordinal)
            };
        }

        private static void ExpandDescendants(HashSet<string> ids, ILookup<string, WorkflowGroupOccurrenceFact> children)
        {
            var pending = new Queue<string>(ids);
            while (pending.TryDequeue(out var parent))
                foreach (var child in children[parent]) if (ids.Add(child.OccurrenceId)) pending.Enqueue(child.OccurrenceId);
        }
    }
}
