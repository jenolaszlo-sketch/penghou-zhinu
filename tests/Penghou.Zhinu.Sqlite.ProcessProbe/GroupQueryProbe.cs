using System.Text.Json;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;

namespace Penghou.Zhinu.Sqlite.ProcessProbe;

internal static class GroupQueryProbe
{
    public static async Task<int> RunAsync(string databasePath, Guid runId, string priorProcessToken, string receiptPath)
    {
        var store = new SqliteWorkflowStore(new ZhinuSqliteOptions
        {
            DatabasePath = databasePath,
            BusyTimeout = TimeSpan.FromSeconds(5),
            Pooling = false
        });
        using var reader = new WorkflowGroupReader(store);
        var opened = await reader.GetRunGroupSnapshotAsync(runId, CancellationToken.None).ConfigureAwait(false);
        var snapshot = opened.Value;
        if (opened.Status != WorkflowGroupReadStatus.Success || snapshot is null) return 10;

        var stale = await reader.QueryGroupDefinitionsAsync(runId, priorProcessToken, cancellationToken: CancellationToken.None)
            .ConfigureAwait(false);
        var definitions = await CollectDefinitionsAsync(reader, runId, snapshot.SnapshotToken).ConfigureAwait(false);
        var rootNodes = await CollectChildrenAsync(reader, runId, snapshot.SnapshotToken, null).ConfigureAwait(false);
        var occurrenceNodes = rootNodes.Where(node => node.Kind == WorkflowGroupNodeKind.Occurrence).ToList();
        var allOccurrences = new List<WorkflowGroupOccurrenceFact>();
        var members = new List<WorkflowGroupMember>();
        var states = new List<WorkflowGroupStateSnapshot>();
        var pending = new Queue<WorkflowGroupOccurrenceFact>(occurrenceNodes.Select(node => node.Occurrence!));
        while (pending.TryDequeue(out var occurrence))
        {
            allOccurrences.Add(occurrence);
            var state = await reader.GetGroupStateAsync(runId, snapshot.SnapshotToken, occurrence.OccurrenceId, CancellationToken.None).ConfigureAwait(false);
            if (state.Status != WorkflowGroupReadStatus.Success || state.Value is null) return 12;
            states.Add(state.Value);
            members.AddRange(await CollectMembersAsync(reader, runId, snapshot.SnapshotToken, occurrence.OccurrenceId)
                .ConfigureAwait(false));
            var children = await CollectChildrenAsync(reader, runId, snapshot.SnapshotToken, occurrence.OccurrenceId)
                .ConfigureAwait(false);
            foreach (var child in children.Where(node => node.Kind == WorkflowGroupNodeKind.Occurrence))
                pending.Enqueue(child.Occurrence!);
        }

        var projectedJson = JsonSerializer.Serialize(new
        {
            Snapshot = snapshot,
            Definitions = definitions,
            Occurrences = allOccurrences,
            Members = members
        });
        var payloadFieldsAbsent = new[] { "\"InputJson\"", "\"OutputJson\"", "\"MetadataJson\"", "\"Error\"" }
            .All(field => !projectedJson.Contains(field, StringComparison.Ordinal));
        var receipt = new
        {
            Scenario = "fresh-process-group-query",
            RunId = runId,
            SnapshotToken = snapshot.SnapshotToken,
            PriorProcessTokenStatus = stale.Status.ToString(),
            ThroughDurableSequence = snapshot.ThroughDurableSequence,
            DefinitionKeys = definitions.Select(item => item.GroupKey).OrderBy(item => item, StringComparer.Ordinal).ToArray(),
            RootOccurrenceCount = rootNodes.Count(node => node.Kind == WorkflowGroupNodeKind.Occurrence),
            Occurrences = allOccurrences.Select(item => new
            {
                item.OccurrenceId,
                item.GroupKey,
                item.ParentOccurrenceId,
                item.ExecutionCoordinate
            }).ToArray(),
            Members = members.Select(item => new
            {
                item.Step.StepKey,
                item.Step.Revision,
                item.Step.GroupOccurrenceId,
                Role = item.Step.Role?.ToString()
            }).ToArray(),
            PayloadFieldsAbsent = payloadFieldsAbsent
            ,
            GroupStates = states.Select(state => new
            {
                state.OccurrenceId,
                Status = state.State.Status?.ToString(),
                state.State.Work,
                state.State.Control,
                state.State.TotalCount,
                state.ObservedAt
            }).ToArray()
        };
        await File.WriteAllTextAsync(receiptPath, JsonSerializer.Serialize(receipt), CancellationToken.None).ConfigureAwait(false);
        return stale.Status == WorkflowGroupReadStatus.ResyncRequired && allOccurrences.Count == 15 && members.Count == 15 ? 0 : 11;
    }

    private static async Task<List<WorkflowGroupDefinitionFact>> CollectDefinitionsAsync(
        IWorkflowGroupReader reader, Guid runId, string token)
    {
        var collected = new List<WorkflowGroupDefinitionFact>();
        var parents = new Queue<string?>();
        parents.Enqueue(null);
        while (parents.TryDequeue(out var parent))
        {
            string? cursor = null;
            do
            {
                var page = await reader.QueryGroupDefinitionsAsync(runId, token, parent, 100, cursor, CancellationToken.None)
                    .ConfigureAwait(false);
                if (page.Status != WorkflowGroupReadStatus.Success || page.Value is null)
                    throw new InvalidOperationException($"Definition query failed: {page.Status}.");
                foreach (var definition in page.Value.Items)
                {
                    collected.Add(definition);
                    parents.Enqueue(definition.GroupKey);
                }
                cursor = page.Value.NextCursor;
            } while (cursor is not null);
        }
        return collected;
    }

    private static async Task<List<WorkflowGroupNode>> CollectChildrenAsync(
        IWorkflowGroupReader reader, Guid runId, string token, string? parentOccurrenceId)
    {
        var collected = new List<WorkflowGroupNode>();
        string? cursor = null;
        do
        {
            var page = await reader.QueryGroupChildrenAsync(runId, token, parentOccurrenceId, 100, cursor, CancellationToken.None)
                .ConfigureAwait(false);
            if (page.Status != WorkflowGroupReadStatus.Success || page.Value is null)
                throw new InvalidOperationException($"Child query failed: {page.Status}.");
            collected.AddRange(page.Value.Items);
            cursor = page.Value.NextCursor;
        } while (cursor is not null);
        return collected;
    }

    private static async Task<List<WorkflowGroupMember>> CollectMembersAsync(
        IWorkflowGroupReader reader, Guid runId, string token, string occurrenceId)
    {
        var collected = new List<WorkflowGroupMember>();
        string? cursor = null;
        do
        {
            var page = await reader.QueryGroupMembersAsync(runId, token, occurrenceId, pageSize: 100, cursor: cursor,
                cancellationToken: CancellationToken.None).ConfigureAwait(false);
            if (page.Status != WorkflowGroupReadStatus.Success || page.Value is null)
                throw new InvalidOperationException($"Member query failed: {page.Status}.");
            collected.AddRange(page.Value.Items);
            cursor = page.Value.NextCursor;
        } while (cursor is not null);
        return collected;
    }
}
