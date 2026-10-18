using System.Security.Cryptography;
using System.Text.Json;

namespace Penghou.Zhinu;

/// <summary>Bounded, immutable hierarchy snapshots for trusted embedded hosts.</summary>
public sealed partial class WorkflowGroupReader : IWorkflowGroupReader, IWorkflowGroupStateReader, IDisposable
{
    private readonly IWorkflowGroupSnapshotRepository? repository;
    private readonly WorkflowGroupQueryOptions options;
    private readonly TimeProvider clock;
    private readonly IWorkflowGroupReadAuthorizer? authorizer;
    private readonly object gate = new();
    private readonly SemaphoreSlim captureGate = new(1, 1);
    private readonly Dictionary<string, Entry> snapshots = new(StringComparer.Ordinal);
    private readonly byte[] secret = RandomNumberGenerator.GetBytes(32);
    private long sequence;
    private long cachedBytes;
    private bool disposed;

    public WorkflowGroupReader(IWorkflowStore store, WorkflowGroupQueryOptions? options = null,
        TimeProvider? timeProvider = null, IWorkflowGroupReadAuthorizer? authorizer = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        this.options = options ?? new();
        this.options.Validate();
        repository = store as IWorkflowGroupSnapshotRepository;
        clock = timeProvider ?? TimeProvider.System;
        this.authorizer = authorizer;
    }

    public async ValueTask<WorkflowGroupQueryResult<WorkflowGroupSnapshot>> GetRunGroupSnapshotAsync(
        Guid workflowRunId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await captureGate.WaitAsync(cancellationToken);
        try { return await CaptureAsync(workflowRunId, cancellationToken); }
        finally { captureGate.Release(); }
    }

    private async ValueTask<WorkflowGroupQueryResult<WorkflowGroupSnapshot>> CaptureAsync(
        Guid workflowRunId, CancellationToken cancellationToken)
    {
        var access = await AuthorizeAsync(workflowRunId, cancellationToken);
        if (!access.HasFlag(WorkflowGroupReadAccess.Structure)) return new(WorkflowGroupReadStatus.Forbidden);
        if (repository is null) return new(WorkflowGroupReadStatus.Unsupported);
        var limits = options.ReadLimits with
        {
            IncludeProvenance = options.ReadLimits.IncludeProvenance && access.HasFlag(WorkflowGroupReadAccess.Provenance),
            IncludeSourceReferences = options.ReadLimits.IncludeSourceReferences && access.HasFlag(WorkflowGroupReadAccess.Provenance)
                && access.HasFlag(WorkflowGroupReadAccess.SourceReference)
        };
        var result = await repository.ReadGroupSnapshotAsync(workflowRunId, limits, cancellationToken);
        if (result.Status != WorkflowGroupReadStatus.Success) return new(result.Status, Reason: result.Reason);
        if (result.Value is null) return new(WorkflowGroupReadStatus.InconsistentData);
        var data = result.Value;
        if (data.Run is null || data.Definitions is null || data.Occurrences is null || data.Steps is null
            || data.Dependencies is null || data.Waits is null || data.Approvals is null || data.Expansions is null
            || data.ChildRuns is null || data.Dispositions is null) return new(WorkflowGroupReadStatus.InconsistentData);
        if (!CollectionsPresent(data)) return new(WorkflowGroupReadStatus.InconsistentData);
        var rows = RowCount(data);
        if (rows > limits.MaximumRows || rows * 256L > limits.MaximumBytes || data.EstimatedBytes > limits.MaximumBytes)
            return new(WorkflowGroupReadStatus.LimitExceeded);
        try
        {
            if (!Valid(data, workflowRunId)) return new(WorkflowGroupReadStatus.InconsistentData);
        }
        catch (WorkflowConfigurationException) { return new(WorkflowGroupReadStatus.InconsistentData); }
        data = Freeze(data, limits);
        using var counter = new CountingStream(limits.MaximumBytes);
        try { JsonSerializer.Serialize(counter, data); }
        catch (SnapshotLimitException) { return new(WorkflowGroupReadStatus.LimitExceeded); }
        // UTF-16 strings and collection/object overhead must be charged as
        // well as wire bytes; this is a conservative cache admission budget.
        var bytes = Math.Max(data.EstimatedBytes, counter.Length * 2 + rows * 256L);
        if (bytes > limits.MaximumBytes) return new(WorkflowGroupReadStatus.LimitExceeded);
        cancellationToken.ThrowIfCancellationRequested();
        var now = clock.GetUtcNow();
        var header = new WorkflowGroupSnapshot(Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), now,
            now + options.SnapshotLifetime, data.ThroughDurableSequence, data.Run, data.Definitions.Count,
            data.Occurrences.Count, data.Steps.Count, data.Expansions.Count, limits.IncludeProvenance,
            limits.IncludeSourceReferences, data.SourceRun, data.Generation, data.Operation, data.Dispositions);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            foreach (var expired in snapshots.Values.Where(e => e.Header.ExpiresAt <= now).ToArray()) Remove(expired.Header.SnapshotToken);
            while (snapshots.Count >= options.MaximumSnapshots || cachedBytes + bytes > options.MaximumCacheBytes)
                Remove(snapshots.Values.MinBy(e => e.Sequence)!.Header.SnapshotToken);
            snapshots.Add(header.SnapshotToken, new(header, data, bytes, ++sequence));
            cachedBytes += bytes;
        }
        return new(WorkflowGroupReadStatus.Success, header);
    }

    public async ValueTask<WorkflowGroupQueryResult<WorkflowGroupNode>> GetGroupOccurrenceAsync(Guid workflowRunId,
        string snapshotToken, string occurrenceId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(occurrenceId);
        var found = await FindAsync(workflowRunId, snapshotToken, cancellationToken);
        if (found.Value is null) return new(found.Status);
        if (occurrenceId.Length > 256) return new(WorkflowGroupReadStatus.OccurrenceNotFound);
        var occurrence = found.Value.Data.Occurrences.FirstOrDefault(o => o.OccurrenceId == occurrenceId);
        return occurrence is null ? new(WorkflowGroupReadStatus.OccurrenceNotFound) : new(WorkflowGroupReadStatus.Success, Node(found.Value.Data, occurrence));
    }

    public async ValueTask<WorkflowGroupQueryResult<WorkflowGroupPage<WorkflowGroupDefinitionFact>>> QueryGroupDefinitionsAsync(
        Guid workflowRunId, string snapshotToken, string? parentGroupKey = null, int pageSize = 100, string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        CheckPage(pageSize);
        var found = await FindAsync(workflowRunId, snapshotToken, cancellationToken);
        if (found.Value is null) return new(found.Status);
        var data = found.Value.Data;
        if (parentGroupKey is not null && (parentGroupKey.Length > 4096 || !data.Definitions.Any(d => d.GroupKey == parentGroupKey))) return new(WorkflowGroupReadStatus.DefinitionNotFound);
        return Page(found.Value, data.Definitions.Where(d => d.ParentGroupKey == parentGroupKey)
            .OrderBy(d => d.ChildOrdinal).ThenBy(d => d.GroupKey, StringComparer.Ordinal), "definitions", parentGroupKey, false, pageSize, cursor);
    }

    public async ValueTask<WorkflowGroupQueryResult<WorkflowGroupPage<WorkflowGroupNode>>> QueryGroupChildrenAsync(
        Guid workflowRunId, string snapshotToken, string? parentOccurrenceId = null, int pageSize = 100, string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        CheckPage(pageSize);
        var found = await FindAsync(workflowRunId, snapshotToken, cancellationToken);
        if (found.Value is null) return new(found.Status);
        var data = found.Value.Data;
        if (parentOccurrenceId is not null && !data.Occurrences.Any(o => o.OccurrenceId == parentOccurrenceId)) return new(WorkflowGroupReadStatus.OccurrenceNotFound);
        var ordinals = data.Definitions.ToDictionary(d => d.GroupKey, d => d.ChildOrdinal, StringComparer.Ordinal);
        var populated = PopulatedOccurrences(data);
        IEnumerable<WorkflowGroupNode> nodes = data.Occurrences.Where(o => o.ParentOccurrenceId == parentOccurrenceId)
            .OrderBy(o => ordinals[o.GroupKey]).ThenBy(o => o.GroupKey, StringComparer.Ordinal)
            .ThenBy(o => o.ExecutionCoordinate, StringComparer.Ordinal).ThenBy(o => o.OccurrenceId, StringComparer.Ordinal)
            .Select(o => new WorkflowGroupNode(WorkflowGroupNodeKind.Occurrence, o, null, null, populated.Contains(o.OccurrenceId)));
        if (parentOccurrenceId is null)
        {
            nodes = nodes.Concat(data.Steps.Where(s => s.IsCurrent && s.GroupOccurrenceId is null)
                .OrderBy(s => s.StepKey, StringComparer.Ordinal).ThenBy(s => s.StepId)
                .Select(s => new WorkflowGroupNode(WorkflowGroupNodeKind.UngroupedStep, null, s, null, false)))
                .Concat(data.ChildRuns.OrderBy(c => c.RunId).Select(c => new WorkflowGroupNode(WorkflowGroupNodeKind.ChildRun, null, null, c, false)));
        }
        return Page(found.Value, nodes, "children", parentOccurrenceId, false, pageSize, cursor);
    }

    public async ValueTask<WorkflowGroupQueryResult<WorkflowGroupPage<WorkflowGroupMember>>> QueryGroupMembersAsync(
        Guid workflowRunId, string snapshotToken, string? occurrenceId = null, bool includeHistory = false,
        int pageSize = 100, string? cursor = null, CancellationToken cancellationToken = default)
    {
        CheckPage(pageSize);
        var found = await FindAsync(workflowRunId, snapshotToken, cancellationToken);
        if (found.Value is null) return new(found.Status);
        var data = found.Value.Data;
        if (occurrenceId is not null && !data.Occurrences.Any(o => o.OccurrenceId == occurrenceId)) return new(WorkflowGroupReadStatus.OccurrenceNotFound);
        var dependencies = data.Dependencies.ToLookup(d => d.StepKey, StringComparer.Ordinal);
        var waits = data.Waits.ToLookup(w => (w.StepId, w.Revision));
        var approvals = data.Approvals.ToLookup(a => (a.StepKey, a.Revision));
        var members = data.Steps.Where(s => s.GroupOccurrenceId == occurrenceId && (includeHistory || s.IsCurrent))
            .OrderBy(s => s.StepKey, StringComparer.Ordinal).ThenBy(s => s.Revision).ThenBy(s => s.StepId)
            .Select(s => new WorkflowGroupMember(s, Copy(dependencies[s.StepKey]), Copy(waits[(s.StepId, s.Revision)]), Copy(approvals[(s.StepKey, s.Revision)])));
        return Page(found.Value, members, "members", occurrenceId, includeHistory, pageSize, cursor);
    }

    public async ValueTask<WorkflowGroupQueryResult<WorkflowGroupPage<WorkflowGroupExpansionFact>>> QueryGroupExpansionsAsync(
        Guid workflowRunId, string snapshotToken, int pageSize = 100, string? cursor = null, CancellationToken cancellationToken = default)
    {
        CheckPage(pageSize);
        var found = await FindAsync(workflowRunId, snapshotToken, cancellationToken);
        if (found.Value is null) return new(found.Status);
        return Page(found.Value, found.Value.Data.Expansions.OrderBy(e => e.FanOutKey, StringComparer.Ordinal)
            .ThenBy(e => e.BoundaryRevision).ThenBy(e => e.ExpansionId, StringComparer.Ordinal), "expansions", null, false, pageSize, cursor);
    }

    private async ValueTask<WorkflowGroupReadAccess> AuthorizeAsync(Guid runId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (runId == Guid.Empty) throw new ArgumentException("Run identifier cannot be empty.", nameof(runId));
        lock (gate) ObjectDisposedException.ThrowIf(disposed, this);
        return authorizer is null ? WorkflowGroupReadAccess.Structure | WorkflowGroupReadAccess.Provenance | WorkflowGroupReadAccess.SourceReference
            : await authorizer.AuthorizeAsync(runId, ct);
    }

    private async ValueTask<WorkflowGroupQueryResult<Entry>> FindAsync(Guid runId, string token, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        var access = await AuthorizeAsync(runId, ct);
        if (!access.HasFlag(WorkflowGroupReadAccess.Structure)) return new(WorkflowGroupReadStatus.Forbidden);
        if (repository is null) return new(WorkflowGroupReadStatus.Unsupported);
        if (token.Length != 64) return new(WorkflowGroupReadStatus.ResyncRequired);
        Entry? entry;
        lock (gate)
        {
            snapshots.TryGetValue(token, out entry);
            if (entry is null || entry.Data.Run.RunId != runId) return new(WorkflowGroupReadStatus.ResyncRequired);
            if (entry.Header.ExpiresAt <= clock.GetUtcNow()
                || (entry.Header.ProvenanceIncluded && !access.HasFlag(WorkflowGroupReadAccess.Provenance))
                || (entry.Header.SourceReferencesIncluded && !access.HasFlag(WorkflowGroupReadAccess.SourceReference)))
            {
                Remove(token);
                return new(WorkflowGroupReadStatus.ResyncRequired);
            }
        }
        var stamp = await repository!.GetGroupSnapshotRunStampAsync(runId, ct);
        if (stamp != entry.Data.RunCreatedSequence)
        {
            lock (gate) Remove(token);
            return new(stamp is null ? WorkflowGroupReadStatus.RunNotFound : WorkflowGroupReadStatus.ResyncRequired);
        }
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!snapshots.ContainsKey(token) || entry.Header.ExpiresAt <= clock.GetUtcNow()) return new(WorkflowGroupReadStatus.ResyncRequired);
        }
        return new(WorkflowGroupReadStatus.Success, entry);
    }

    private WorkflowGroupQueryResult<WorkflowGroupPage<T>> Page<T>(Entry entry, IEnumerable<T> source,
        string kind, string? parent, bool history, int size, string? cursor)
    {
        var expected = new Cursor(entry.Header.SnapshotToken, kind, parent, history, size, 0);
        var offset = 0;
        if (cursor is not null)
        {
            if (cursor.Length > 4096) return new(WorkflowGroupReadStatus.ResyncRequired);
            try
            {
                var parts = cursor.Split('.');
                if (parts.Length != 2) return new(WorkflowGroupReadStatus.ResyncRequired);
                var bytes = Convert.FromBase64String(parts[0]);
                if (!CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(secret, bytes), Convert.FromBase64String(parts[1])))
                    return new(WorkflowGroupReadStatus.ResyncRequired);
                var decoded = JsonSerializer.Deserialize<Cursor>(bytes);
                if (decoded is null || decoded with { Offset = 0 } != expected || decoded.Offset < 0)
                    return new(WorkflowGroupReadStatus.ResyncRequired);
                offset = decoded.Offset;
            }
            catch (Exception ex) when (ex is FormatException or JsonException) { return new(WorkflowGroupReadStatus.ResyncRequired); }
        }
        var total = source.Count();
        if (offset > total) return new(WorkflowGroupReadStatus.ResyncRequired);
        var items = Copy(source.Skip(offset).Take(size));
        var more = offset + items.Count < total;
        string? next = null;
        if (more)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(expected with { Offset = offset + items.Count });
            next = Convert.ToBase64String(bytes) + "." + Convert.ToBase64String(HMACSHA256.HashData(secret, bytes));
        }
        return new(WorkflowGroupReadStatus.Success, new(items, next, more, total, entry.Header.SnapshotToken, entry.Data.ThroughDurableSequence));
    }

    private static WorkflowGroupNode Node(WorkflowGroupSnapshotData data, WorkflowGroupOccurrenceFact occurrence) =>
        new(WorkflowGroupNodeKind.Occurrence, occurrence, null, null,
            data.Occurrences.Any(o => o.ParentOccurrenceId == occurrence.OccurrenceId) || data.Steps.Any(s => s.GroupOccurrenceId == occurrence.OccurrenceId));

    private static HashSet<string> PopulatedOccurrences(WorkflowGroupSnapshotData data) =>
        data.Occurrences.Select(o => o.ParentOccurrenceId).Concat(data.Steps.Select(s => s.GroupOccurrenceId))
            .OfType<string>().ToHashSet(StringComparer.Ordinal);

    private static bool Valid(WorkflowGroupSnapshotData d, Guid runId)
    {
        if (d.Run.RunId != runId || !Enum.IsDefined(d.Run.Status) || d.EstimatedBytes < 0 || d.ThroughDurableSequence < 0 || d.RunCreatedSequence < 0) return false;
        var definitions = new Dictionary<string, WorkflowGroupDefinitionFact>(StringComparer.Ordinal);
        foreach (var def in d.Definitions)
            if (string.IsNullOrWhiteSpace(def.GroupKey) || def.ChildOrdinal < 0 || def.OccurrenceCount < 0 || !Enum.IsDefined(def.Kind)
                || (def.Kind == ActivityGroupKind.Logical && def.Provenance is not null) || !definitions.TryAdd(def.GroupKey, def)) return false;
        if (d.Definitions.GroupBy(def => (def.ParentGroupKey, def.ChildOrdinal)).Any(g => g.Count() > 1)) return false;
        if (d.ChildRuns.Any(child => child.RunId == Guid.Empty || child.RunId == runId)
            || d.ChildRuns.Select(child => child.RunId).Distinct().Count() != d.ChildRuns.Count) return false;
        foreach (var def in definitions.Values)
        {
            var current = def;
            var visited = new HashSet<string>(StringComparer.Ordinal);
            while (true)
            {
                if (!visited.Add(current.GroupKey) || visited.Count > 128) return false;
                if (current.ParentGroupKey is null) break;
                if (!definitions.TryGetValue(current.ParentGroupKey, out current!)) return false;
            }
        }
        var occurrences = new Dictionary<string, WorkflowGroupOccurrenceFact>(StringComparer.Ordinal);
        var coordinates = new Dictionary<string, IReadOnlyList<ActivityGroupCoordinateFrame>>(StringComparer.Ordinal);
        foreach (var o in d.Occurrences)
        {
            if (o.WorkflowRunId != runId || string.IsNullOrWhiteSpace(o.OccurrenceId) || o.ExecutionCoordinate is null || o.ExecutionCoordinate.Length > 32768
                || string.IsNullOrWhiteSpace(o.GroupKey) || !definitions.ContainsKey(o.GroupKey) || !occurrences.TryAdd(o.OccurrenceId, o)
                || ActivityGroupOccurrence.ComputeId(runId, o.GroupKey, o.ParentOccurrenceId, o.ExecutionCoordinate) != o.OccurrenceId) return false;
            coordinates.Add(o.OccurrenceId, ActivityGroupOccurrence.ReadCoordinate(o.ExecutionCoordinate));
        }
        if (occurrences.Count > ActivityGroupOccurrence.MaximumAllowedOccurrencesPerRun) return false;
        var counts = occurrences.Values.GroupBy(o => o.GroupKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        if (definitions.Values.Any(definition => definition.OccurrenceCount != counts.GetValueOrDefault(definition.GroupKey))) return false;
        foreach (var o in occurrences.Values)
        {
            var current = o;
            var visited = new HashSet<string>(StringComparer.Ordinal);
            while (true)
            {
                if (!visited.Add(current.OccurrenceId) || visited.Count > 128) return false;
                if (current.ParentOccurrenceId is null)
                {
                    if (definitions[current.GroupKey].ParentGroupKey is not null) return false;
                    break;
                }
                if (!occurrences.TryGetValue(current.ParentOccurrenceId, out var parent) || definitions[current.GroupKey].ParentGroupKey != parent.GroupKey) return false;
                var parentFrames = coordinates[parent.OccurrenceId];
                var frames = coordinates[current.OccurrenceId];
                if (parentFrames.Count > frames.Count || !parentFrames.SequenceEqual(frames.Take(parentFrames.Count))) return false;
                current = parent;
            }
        }
        var identities = new HashSet<Guid>();
        var keys = new HashSet<(string, int)>();
        foreach (var s in d.Steps)
            if (s.WorkflowRunId != runId || s.StepId == Guid.Empty || string.IsNullOrWhiteSpace(s.StepKey)
                || s.Revision < 1 || s.Attempt < 0 || !Enum.IsDefined(s.Status) || !identities.Add(s.StepId) || !keys.Add((s.StepKey, s.Revision))
                || (s.GroupOccurrenceId is null ? s.Role is not null : s.Role is null || !Enum.IsDefined(s.Role.Value) || !occurrences.ContainsKey(s.GroupOccurrenceId))) return false;
        foreach (var steps in d.Steps.GroupBy(s => s.StepKey, StringComparer.Ordinal))
            if (steps.Count(s => s.IsCurrent) != 1 || steps.Single(s => s.IsCurrent).Revision != steps.Max(s => s.Revision)) return false;
        return ValidExpansions(d, occurrences);
    }

    private static bool CollectionsPresent(WorkflowGroupSnapshotData d) =>
        d.Definitions.All(item => item is not null) && d.Occurrences.All(item => item is not null)
        && d.Steps.All(item => item is not null) && d.Dependencies.All(item => item is not null)
        && d.Waits.All(item => item is not null) && d.Approvals.All(item => item is not null)
        && d.ChildRuns.All(item => item is not null) && d.Dispositions.All(item => item is not null)
        && d.Expansions.All(expansion => expansion is not null && expansion.Items is not null && expansion.Closures is not null
            && expansion.Items.All(item => item is not null)
            && expansion.Closures.All(closure => closure is not null && closure.Steps is not null
                && closure.Steps.All(step => step is not null)));

    private static bool ValidExpansions(WorkflowGroupSnapshotData d,
        IReadOnlyDictionary<string, WorkflowGroupOccurrenceFact> occurrences)
    {
        var steps = d.Steps.ToDictionary(step => step.StepId);
        var current = d.Steps.Where(step => step.IsCurrent).ToDictionary(step => step.StepKey, StringComparer.Ordinal);
        var expansions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var expansion in d.Expansions)
        {
            if (string.IsNullOrWhiteSpace(expansion.ExpansionId) || !expansions.Add(expansion.ExpansionId)
                || string.IsNullOrWhiteSpace(expansion.FanOutKey) || string.IsNullOrWhiteSpace(expansion.PlanDigest)
                || expansion.ItemCount != expansion.Items.Count
                || !steps.TryGetValue(expansion.BoundaryStepId, out var boundary)
                || boundary.Revision != expansion.BoundaryRevision || boundary.IsCurrent != expansion.IsCurrent) return false;
            var itemKeys = new HashSet<string>(StringComparer.Ordinal);
            var stepKeys = new HashSet<string>(StringComparer.Ordinal);
            var occurrenceIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in expansion.Items)
                if (string.IsNullOrWhiteSpace(item.ItemKey) || !itemKeys.Add(item.ItemKey)
                    || string.IsNullOrWhiteSpace(item.StepKey) || !stepKeys.Add(item.StepKey)
                    || string.IsNullOrWhiteSpace(item.OccurrenceId) || !occurrenceIds.Add(item.OccurrenceId)
                    || !occurrences.ContainsKey(item.OccurrenceId) || !Enum.IsDefined(item.Role)) return false;
            foreach (var closure in expansion.Closures)
                if (string.IsNullOrWhiteSpace(closure.Digest) || closure.Steps.Count != expansion.ItemCount
                    || closure.Steps.Select(step => step.StepId).Distinct().Count() != closure.Steps.Count
                    || closure.Steps.Any(step => !steps.TryGetValue(step.StepId, out var stored) || stored.Revision != step.Revision)) return false;
            if (!expansion.IsComplete) continue;
            if (!expansion.IsCurrent || expansion.Items.Any(item => !current.TryGetValue(item.StepKey, out var step)
                || step.Status != StepStatus.Completed || step.GroupOccurrenceId != item.OccurrenceId || step.Role != item.Role)) return false;
            // Provider closure must reference the exact current selected revisions.
            if (!expansion.Closures.Any(closure => closure.Steps.Select(step => (step.StepId, step.Revision)).ToHashSet()
                .SetEquals(expansion.Items.Select(item => (current[item.StepKey].StepId, current[item.StepKey].Revision))))) return false;
        }
        return true;
    }

    private static long RowCount(WorkflowGroupSnapshotData d) => 1L + d.Definitions.Count + d.Occurrences.Count + d.Steps.Count
        + d.Dependencies.Count + d.Waits.Count + d.Approvals.Count + d.ChildRuns.Count + d.Dispositions.Count
        + (d.SourceRun is null ? 0 : 1) + (d.Generation is null ? 0 : 1) + (d.Operation is null ? 0 : 1)
        + d.Expansions.Sum(e => 1L + e.Items.Count + e.Closures.Sum(c => 1L + c.Steps.Count));

    private static WorkflowGroupSnapshotData Freeze(WorkflowGroupSnapshotData d, WorkflowGroupReadLimits limits) => d with
    {
        Definitions = Copy(d.Definitions.Select(v => v with { Provenance = !limits.IncludeProvenance ? null : v.Provenance is null ? null : v.Provenance with { SourceReference = limits.IncludeSourceReferences ? v.Provenance.SourceReference : null } })),
        Occurrences = Copy(d.Occurrences),
        Steps = Copy(d.Steps),
        Dependencies = Copy(d.Dependencies),
        Waits = Copy(d.Waits),
        Approvals = Copy(d.Approvals),
        ChildRuns = Copy(d.ChildRuns),
        Dispositions = Copy(d.Dispositions),
        Expansions = Copy(d.Expansions.Select(e => e with { Items = Copy(e.Items), Closures = Copy(e.Closures.Select(c => c with { Steps = Copy(c.Steps) })) }))
    };

    private static IReadOnlyList<T> Copy<T>(IEnumerable<T> values) => values.ToList().AsReadOnly();
    private static void CheckPage(int size) { if (size is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(size)); }
    private void Remove(string token) { if (snapshots.Remove(token, out var entry)) cachedBytes -= entry.Bytes; }

    public void Dispose()
    {
        lock (gate) { disposed = true; snapshots.Clear(); cachedBytes = 0; }
    }

    private sealed record Entry(WorkflowGroupSnapshot Header, WorkflowGroupSnapshotData Data, long Bytes, long Sequence);
    private sealed record Cursor(string Token, string Kind, string? Parent, bool History, int Size, int Offset);

    private sealed class SnapshotLimitException : Exception;
    private sealed class CountingStream(long maximum) : Stream
    {
        private long count;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => count;
        public override long Position { get => count; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override void Write(byte[] buffer, int offset, int size) => Charge(size);
        public override void Write(ReadOnlySpan<byte> buffer) => Charge(buffer.Length);
        private void Charge(int size) { if (count + size > maximum) throw new SnapshotLimitException(); count += size; }
        public override int Read(byte[] buffer, int offset, int size) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
