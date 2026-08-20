namespace Penghou.Zhinu;

/// <summary>Persists immutable artifact references and their provenance.</summary>
public interface IWorkflowArtifactRepository
{
    /// <summary>
    /// Publishes an artifact. Repeating an identical publication in the same
    /// run/step revision is idempotent; conflicting data is rejected.
    /// </summary>
    ValueTask<WorkflowArtifactReference> PublishArtifactAsync(
        ArtifactPublicationRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<WorkflowArtifactReference?> GetArtifactAsync(
        Guid artifactId,
        CancellationToken cancellationToken = default);

    /// <summary>Returns every artifact revision produced by a run.</summary>
    ValueTask<IReadOnlyList<WorkflowArtifactReference>> GetArtifactsAsync(
        Guid workflowRunId,
        CancellationToken cancellationToken = default);
}
