namespace Penghou.Zhinu;

/// <summary>Persists immutable artifact references and their provenance.</summary>
public interface IWorkflowArtifactRepository
{
    /// <summary>
    /// Publishes an artifact. Repeating an identical publication in the same
    /// run/step revision is idempotent; conflicting data is rejected.
    /// </summary>
    ValueTask<ArtifactPublicationResult> PublishArtifactAsync(
        ArtifactPublicationRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<WorkflowArtifactReference?> GetArtifactAsync(
        Guid artifactId,
        CancellationToken cancellationToken = default);

    /// <summary>Returns every artifact revision produced by a run.</summary>
    ValueTask<IReadOnlyList<WorkflowArtifactReference>> GetArtifactsAsync(
        Guid workflowRunId,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<WorkflowArtifactReference>> QueryArtifactsAsync(
        Guid workflowRunId,
        ArtifactQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the latest servable revision of a named artifact: revisions
    /// tombstoned by artifact invalidation are skipped, while revisions
    /// flagged by evidence invalidation keep serving pending revalidation.
    /// </summary>
    ValueTask<WorkflowArtifactReference?> GetLatestArtifactAsync(
        Guid workflowRunId,
        string name,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records an invalidation on an existing artifact revision and emits an
    /// invalidation event atomically. Missing artifacts fail with
    /// <see cref="WorkflowNotFoundException"/>. Records are append-only.
    /// </summary>
    ValueTask<ArtifactInvalidation> InvalidateArtifactAsync(
        Guid artifactId,
        ArtifactInvalidationKind kind,
        string? reason,
        string? actor,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    /// <summary>Lists an artifact revision's invalidations in recorded order.</summary>
    ValueTask<IReadOnlyList<ArtifactInvalidation>> GetInvalidationsAsync(
        Guid artifactId,
        CancellationToken cancellationToken = default);
}
