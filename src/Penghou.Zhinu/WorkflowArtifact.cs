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

/// <summary>Filters and pages artifact references belonging to one workflow run.</summary>
public sealed class ArtifactQuery
{
    public string? Name { get; set; }
    public string? ArtifactType { get; set; }
    public string? ProducerStepKey { get; set; }
    public bool LatestOnly { get; set; }
    public int Offset { get; set; }
    public int Limit { get; set; } = 100;

    public void Validate()
    {
        if (Name is not null)
            ArgumentException.ThrowIfNullOrWhiteSpace(Name);
        if (ArtifactType is not null)
            ArgumentException.ThrowIfNullOrWhiteSpace(ArtifactType);
        if (ProducerStepKey is not null)
            ArgumentException.ThrowIfNullOrWhiteSpace(ProducerStepKey);
        if (Offset < 0)
            throw new ArgumentOutOfRangeException(nameof(Offset));
        if (Limit is < 1 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(Limit));
    }
}

/// <summary>Context supplied to application artifact validators.</summary>
public sealed record ArtifactValidationContext
{
    public required Guid WorkflowRunId { get; init; }
    public string? ProducerStepKey { get; init; }
    public int? ProducerStepRevision { get; init; }
}

/// <summary>
/// Application hook for enforcing artifact naming, location, identity, or
/// metadata policies before a reference is durably published.
/// </summary>
public interface IWorkflowArtifactValidator
{
    ValueTask ValidateAsync(
        WorkflowArtifactDescriptor artifact,
        ArtifactValidationContext context,
        CancellationToken cancellationToken = default);
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

/// <summary>Atomic result of publishing an artifact reference and its event.</summary>
public sealed record ArtifactPublicationResult
{
    public required WorkflowArtifactReference Artifact { get; init; }
    public WorkflowEvent? Event { get; init; }
    public required bool Created { get; init; }
}
