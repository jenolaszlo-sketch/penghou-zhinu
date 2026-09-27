namespace Penghou.Zhinu;

/// <summary>Lifecycle of one execution generation within a workflow instance.</summary>
public enum WorkflowGenerationStatus
{
    /// <summary>The generation was recorded but not yet prepared.</summary>
    Created = 0,
    /// <summary>The candidate plan was evaluated without changing the owner.</summary>
    Prepared = 1,
    /// <summary>This generation owns forward progression.</summary>
    Active = 2,
    /// <summary>A newer generation owns forward progression.</summary>
    Superseded = 3,
    /// <summary>The candidate was rejected and never became active.</summary>
    Rejected = 4,
    /// <summary>
    /// The owner is paused: it retains progression ownership but schedules no
    /// new work. A quiescing generation resumes to active or is superseded at
    /// cutover; it never reactivates from superseded.
    /// </summary>
    Quiescing = 5
}

/// <summary>Stable identity for one logical workflow across replans.</summary>
public sealed record WorkflowInstance
{
    public required Guid InstanceId { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public string? MetadataJson { get; init; }
}

/// <summary>
/// Review-gate decision on a checkpointed generation. Zhinu records the
/// decision; it never invokes an AI planner. <see cref="Accept"/> means the
/// reviewer proceeds (activation stays an explicit, atomic operation);
/// <see cref="Retry"/> means the candidate is sent back for re-evaluation;
/// <see cref="Replan"/> requests external planning.
/// </summary>
public enum CheckpointDisposition
{
    /// <summary>The generation is accepted; activation remains explicit.</summary>
    Accept = 0,
    /// <summary>The candidate returns for re-evaluation.</summary>
    Retry = 1,
    /// <summary>External planning is requested; Zhinu plans nothing.</summary>
    Replan = 2
}

/// <summary>
/// One recorded checkpoint disposition. Decisions are append-only audit:
/// recording never transitions the generation itself.
/// </summary>
public sealed record GenerationDisposition
{
    public required Guid DispositionId { get; init; }
    public required Guid GenerationId { get; init; }
    public required CheckpointDisposition Disposition { get; init; }
    public string? Reason { get; init; }
    public string? Actor { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}

/// <summary>
/// Immutable generation record binding one Zhinu run to an admitted plan
/// revision. Plan revision and execution fingerprint are opaque identities
/// supplied by the planning layer; Zhinu never interprets them.
/// </summary>
public sealed record WorkflowGeneration
{
    public required Guid GenerationId { get; init; }
    public required Guid InstanceId { get; init; }
    public required long Ordinal { get; init; }
    public required Guid WorkflowRunId { get; init; }
    public string? PlanRevision { get; init; }
    public string? ExecutionFingerprint { get; init; }
    public required WorkflowGenerationStatus Status { get; init; }
    public Guid? PredecessorGenerationId { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? ActivatedAt { get; init; }
    public DateTimeOffset? SupersededAt { get; init; }
}
