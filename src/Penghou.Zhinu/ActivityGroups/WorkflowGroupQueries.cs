using Penghou.Workflow.Abstractions;

namespace Penghou.Zhinu;

public enum WorkflowGroupReadStatus
{
    Success, RunNotFound, OccurrenceNotFound, DefinitionNotFound, Unsupported,
    LimitExceeded, ResyncRequired, Forbidden, InconsistentData
}

[Flags]
public enum WorkflowGroupReadAccess { None = 0, Structure = 1, Provenance = 2, SourceReference = 4 }

/// <summary>Host read policy, evaluated on every request. Execution authorization and tokens do not grant read access.</summary>
public interface IWorkflowGroupReadAuthorizer
{
    ValueTask<WorkflowGroupReadAccess> AuthorizeAsync(Guid workflowRunId, CancellationToken cancellationToken = default);
}

public sealed record WorkflowGroupReadLimits
{
    public int MaximumRows { get; init; } = 262_144;
    public long MaximumBytes { get; init; } = 33_554_432;
    public bool IncludeProvenance { get; init; }
    public bool IncludeSourceReferences { get; init; }

    public void Validate()
    {
        if (MaximumRows is < 1 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(MaximumRows));
        if (MaximumBytes is < 1 or > 536_870_912) throw new ArgumentOutOfRangeException(nameof(MaximumBytes));
        if (IncludeSourceReferences && !IncludeProvenance) throw new ArgumentException("Source references require provenance.");
    }
}

public sealed record WorkflowGroupQueryOptions
{
    public WorkflowGroupReadLimits ReadLimits { get; init; } = new();
    public TimeSpan SnapshotLifetime { get; init; } = TimeSpan.FromMinutes(2);
    public int MaximumSnapshots { get; init; } = 8;
    public long MaximumCacheBytes { get; init; } = 134_217_728;

    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(ReadLimits);
        ReadLimits.Validate();
        if (SnapshotLifetime < TimeSpan.FromSeconds(1) || SnapshotLifetime > TimeSpan.FromHours(1))
            throw new ArgumentOutOfRangeException(nameof(SnapshotLifetime));
        if (MaximumSnapshots is < 1 or > 128) throw new ArgumentOutOfRangeException(nameof(MaximumSnapshots));
        if (MaximumCacheBytes < ReadLimits.MaximumBytes || MaximumCacheBytes > 536_870_912)
            throw new ArgumentOutOfRangeException(nameof(MaximumCacheBytes));
    }
}

public sealed record WorkflowGroupRunFact(Guid RunId, string WorkflowName, string WorkflowVersion, WorkflowStatus Status,
    Guid? ParentRunId, Guid? SourceRunId, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    DateTimeOffset? Deadline, long LeaseGeneration, DateTimeOffset? LeaseExpiresAt, string? ManifestDigest, bool GroupFanOutUsed);

public sealed record WorkflowGroupProvenance(string MacroName, string MacroDefinitionFingerprint, string InvocationKey,
    string? MacroVersion, string? SourceReference);

/// <summary>Definition intent. OccurrenceCount includes retained runtime history, not an active-set/progress count.</summary>
public sealed record WorkflowGroupDefinitionFact(string GroupKey, string? ParentGroupKey, ActivityGroupKind Kind,
    string DisplayName, int ChildOrdinal, WorkflowGroupProvenance? Provenance, int OccurrenceCount);

public sealed record WorkflowGroupOccurrenceFact(Guid WorkflowRunId, string OccurrenceId, string GroupKey,
    string? ParentOccurrenceId, string ExecutionCoordinate);

public sealed record WorkflowGroupStepFact(Guid WorkflowRunId, Guid StepId, string StepKey, int Revision, int Attempt,
    StepStatus Status, bool IsCurrent, string? GroupOccurrenceId, ActivityGroupMemberRole? Role,
    long LeaseGeneration, DateTimeOffset? LeaseExpiresAt, DateTimeOffset? AvailableAt,
    DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt, Guid? ChildRunId);

public sealed record WorkflowGroupWaitFact(Guid WaitId, Guid StepId, string StepKey, int Revision,
    WaitKind Kind, WaitStatus Status, Guid? ChildRunId, DateTimeOffset? DeadlineAt, DateTimeOffset? AvailableAt);

public sealed record WorkflowGroupApprovalFact(string StepKey, int Revision, bool IsCompensation,
    ExecutionAuthorizationDecision Decision, bool Ready);

public sealed record WorkflowGroupGenerationFact(Guid GenerationId, Guid InstanceId, long Ordinal,
    WorkflowGenerationStatus Status, Guid? PredecessorGenerationId);

public sealed record WorkflowGroupDispositionFact(Guid DispositionId, CheckpointDisposition Disposition, DateTimeOffset CreatedAt);

public sealed record WorkflowGroupOperationFact(Guid OperationId, string OperationType, WorkflowOperationStatus Status);

/// <summary>A link only. ParentRunId retains actual native ownership, including a reused fork-source child.</summary>
public sealed record WorkflowGroupChildRunFact(Guid RunId, Guid? ParentRunId, WorkflowStatus? Status, bool IsAvailable);

public sealed record WorkflowGroupFanOutItemFact(string ItemKey, string StepKey, string OccurrenceId, ActivityGroupMemberRole Role);
public sealed record WorkflowGroupRevisionFact(Guid StepId, int Revision);
public sealed record WorkflowGroupClosureFact(string Digest, long LeaseGeneration, DateTimeOffset CompletedAt,
    IReadOnlyList<WorkflowGroupRevisionFact> Steps);

/// <summary>IsComplete is an effective current-revision closure fact, not aggregate group lifecycle.</summary>
public sealed record WorkflowGroupExpansionFact(string ExpansionId, string FanOutKey, Guid BoundaryStepId,
    int BoundaryRevision, string PlanDigest, int ItemCount, bool IsCurrent, bool IsComplete,
    IReadOnlyList<WorkflowGroupFanOutItemFact> Items, IReadOnlyList<WorkflowGroupClosureFact> Closures);

/// <summary>Payload-free facts from one storage transaction. Providers must enforce row/byte limits before materializing large fields.</summary>
public sealed record WorkflowGroupSnapshotData
{
    public required WorkflowGroupRunFact Run { get; init; }
    public required long ThroughDurableSequence { get; init; }
    public required long RunCreatedSequence { get; init; }
    public required long EstimatedBytes { get; init; }
    public IReadOnlyList<WorkflowGroupDefinitionFact> Definitions { get; init; } = [];
    public IReadOnlyList<WorkflowGroupOccurrenceFact> Occurrences { get; init; } = [];
    public IReadOnlyList<WorkflowGroupStepFact> Steps { get; init; } = [];
    public IReadOnlyList<StepDependency> Dependencies { get; init; } = [];
    public IReadOnlyList<WorkflowGroupWaitFact> Waits { get; init; } = [];
    public IReadOnlyList<WorkflowGroupApprovalFact> Approvals { get; init; } = [];
    public IReadOnlyList<WorkflowGroupExpansionFact> Expansions { get; init; } = [];
    public IReadOnlyList<WorkflowGroupChildRunFact> ChildRuns { get; init; } = [];
    public WorkflowGroupRunFact? SourceRun { get; init; }
    public WorkflowGroupGenerationFact? Generation { get; init; }
    public WorkflowGroupOperationFact? Operation { get; init; }
    public IReadOnlyList<WorkflowGroupDispositionFact> Dispositions { get; init; } = [];
}

public sealed record WorkflowGroupQueryResult<T>(WorkflowGroupReadStatus Status, T? Value = null, string? Reason = null) where T : class;

/// <summary>Optional storage read capability. Both snapshot capture and creation-stamp lookup are read-only.</summary>
public interface IWorkflowGroupSnapshotRepository
{
    ValueTask<WorkflowGroupQueryResult<WorkflowGroupSnapshotData>> ReadGroupSnapshotAsync(Guid workflowRunId,
        WorkflowGroupReadLimits limits, CancellationToken cancellationToken = default);
    /// <summary>Null means purged/missing; a changed creation-event sequence requires resync even when a RunId is reused.</summary>
    ValueTask<long?> GetGroupSnapshotRunStampAsync(Guid workflowRunId, CancellationToken cancellationToken = default);
}

public sealed record WorkflowGroupSnapshot(string SnapshotToken, DateTimeOffset CapturedAt, DateTimeOffset ExpiresAt,
    long ThroughDurableSequence, WorkflowGroupRunFact Run, int DefinitionCount, int OccurrenceCount,
    int StepRevisionCount, int ExpansionCount, bool ProvenanceIncluded, bool SourceReferencesIncluded,
    WorkflowGroupRunFact? SourceRun, WorkflowGroupGenerationFact? Generation, WorkflowGroupOperationFact? Operation,
    IReadOnlyList<WorkflowGroupDispositionFact> Dispositions);

public sealed record WorkflowGroupMember(WorkflowGroupStepFact Step, IReadOnlyList<StepDependency> Dependencies,
    IReadOnlyList<WorkflowGroupWaitFact> Waits, IReadOnlyList<WorkflowGroupApprovalFact> Approvals);

public enum WorkflowGroupNodeKind { Occurrence, UngroupedStep, ChildRun }
public sealed record WorkflowGroupNode(WorkflowGroupNodeKind Kind, WorkflowGroupOccurrenceFact? Occurrence,
    WorkflowGroupStepFact? Step, WorkflowGroupChildRunFact? ChildRun, bool HasChildren);

/// <summary>TotalCount is exact structural coverage of this query in the cached snapshot, never a progress total.</summary>
public sealed record WorkflowGroupPage<T>(IReadOnlyList<T> Items, string? NextCursor, bool HasMore, int TotalCount,
    string SnapshotToken, long ThroughDurableSequence);

/// <summary>Trusted host read surface. Child runs need their own snapshot; tokens/cursors are not read permissions.</summary>
public interface IWorkflowGroupReader
{
    ValueTask<WorkflowGroupQueryResult<WorkflowGroupSnapshot>> GetRunGroupSnapshotAsync(Guid workflowRunId, CancellationToken cancellationToken = default);
    ValueTask<WorkflowGroupQueryResult<WorkflowGroupNode>> GetGroupOccurrenceAsync(Guid workflowRunId, string snapshotToken,
        string occurrenceId, CancellationToken cancellationToken = default);
    ValueTask<WorkflowGroupQueryResult<WorkflowGroupPage<WorkflowGroupDefinitionFact>>> QueryGroupDefinitionsAsync(Guid workflowRunId,
        string snapshotToken, string? parentGroupKey = null, int pageSize = 100, string? cursor = null, CancellationToken cancellationToken = default);
    ValueTask<WorkflowGroupQueryResult<WorkflowGroupPage<WorkflowGroupNode>>> QueryGroupChildrenAsync(Guid workflowRunId,
        string snapshotToken, string? parentOccurrenceId = null, int pageSize = 100, string? cursor = null, CancellationToken cancellationToken = default);
    ValueTask<WorkflowGroupQueryResult<WorkflowGroupPage<WorkflowGroupMember>>> QueryGroupMembersAsync(Guid workflowRunId,
        string snapshotToken, string? occurrenceId = null, bool includeHistory = false, int pageSize = 100, string? cursor = null,
        CancellationToken cancellationToken = default);
    ValueTask<WorkflowGroupQueryResult<WorkflowGroupPage<WorkflowGroupExpansionFact>>> QueryGroupExpansionsAsync(Guid workflowRunId,
        string snapshotToken, int pageSize = 100, string? cursor = null, CancellationToken cancellationToken = default);
}
