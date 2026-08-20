namespace Penghou.Zhinu;

/// <summary>
/// Describes an externally stored artifact that a workflow wants to publish.
/// Zhinu persists the reference and provenance, not the artifact contents.
/// </summary>
public sealed record WorkflowArtifactDescriptor
{
    public required string Name { get; init; }
    public required string ArtifactType { get; init; }
    public required string Location { get; init; }
    public string? ArtifactVersion { get; init; }
    public string? ContentHash { get; init; }
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
}

/// <summary>An immutable, durable reference to a workflow-produced artifact.</summary>
public sealed record WorkflowArtifactReference
{
    public required Guid Id { get; init; }
    public required Guid WorkflowRunId { get; init; }
    public required string Name { get; init; }
    public required int Revision { get; init; }
    public required string ArtifactType { get; init; }
    public required string Location { get; init; }
    public string? ArtifactVersion { get; init; }
    public string? ContentHash { get; init; }
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
    public string? ProducerStepKey { get; init; }
    public int? ProducerStepRevision { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}

/// <summary>
/// Optional convention for workflow outputs that expose the artifacts they
/// produced. Workflow outputs remain ordinary strongly typed values.
/// </summary>
public interface IArtifactProducingOutput
{
    IReadOnlyList<WorkflowArtifactReference> Artifacts { get; }
}

/// <summary>Publication details passed atomically to a durable store.</summary>
public sealed record ArtifactPublicationRequest
{
    public required Guid WorkflowRunId { get; init; }
    public Guid? StepExecutionId { get; init; }
    public string? ProducerStepKey { get; init; }
    public int? ProducerStepRevision { get; init; }
    public required WorkflowArtifactDescriptor Artifact { get; init; }
    public required DateTimeOffset Now { get; init; }
}
