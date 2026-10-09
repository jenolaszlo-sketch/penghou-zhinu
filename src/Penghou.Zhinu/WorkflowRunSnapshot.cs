namespace Penghou.Zhinu;

/// <summary>
/// An authoritatively consistent run projection snapshot: every row comes from
/// one consistent storage read boundary (a single deferred read transaction),
/// and <see cref="ThroughDurableSequence"/> is the highest durable event
/// sequence visible in that same boundary. No row reflects a durable transition
/// after <see cref="ThroughDurableSequence"/>; advisory events with higher
/// sequences may exist without invalidating the snapshot.
/// <para>
/// Steps are current-revision rows only (no historical step revisions).
/// Diagnosis is derived deterministically from the same boundary's rows.
/// Watermark <see cref="ThroughDurableSequence"/> is durable-state consistency,
/// not a projection watermark and not snapshot atomicity for future reads.
/// </para>
/// </summary>
public sealed record WorkflowRunSnapshot
{
    public required WorkflowRun Run { get; init; }

    public IReadOnlyList<WorkflowStepRun> Steps { get; init; } = [];

    public IReadOnlyList<StepDependency> Dependencies { get; init; } = [];

    public IReadOnlyList<WorkflowWait> Waits { get; init; } = [];

    public IReadOnlyList<WorkflowArtifactReference> Artifacts { get; init; } = [];

    public IReadOnlyList<WorkflowExternalOperation> ExternalOperations { get; init; } = [];

    public WorkflowRunOperation? ActiveOperation { get; init; }

    public WorkflowGeneration? Generation { get; init; }

    public WorkflowInstance? Instance { get; init; }

    public IReadOnlyList<GenerationDisposition> Dispositions { get; init; } = [];

    /// <summary>The source run for a fork, when it is still retained.</summary>
    public WorkflowRun? SourceRun { get; init; }

    /// <summary>
    /// Retained fork ancestors, nearest source first, capped by
    /// <see cref="RunSnapshotOptions.SourceLineageMaxDepth"/>.
    /// </summary>
    public IReadOnlyList<WorkflowRun> SourceLineage { get; init; } = [];

    /// <summary>Child snapshots up to <see cref="RunSnapshotOptions.MaxDepth"/>.</summary>
    public IReadOnlyList<WorkflowRunSnapshot> Children { get; init; } = [];

    /// <summary>
    /// Diagnosis derived from the same boundary's rows. It explains the run's
    /// current state; it is not a scheduling decision.
    /// </summary>
    public RunDiagnosis? Diagnosis { get; init; }

    /// <summary>
    /// Highest durable (<see cref="WorkflowEventDurability.Durable"/>) event
    /// sequence visible in the same read boundary as the rows above. Events
    /// after it may be consumed with the event-page reader.
    /// </summary>
    public required long ThroughDurableSequence { get; init; }
}
