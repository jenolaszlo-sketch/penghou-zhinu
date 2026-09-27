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
