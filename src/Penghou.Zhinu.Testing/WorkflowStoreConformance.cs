namespace Penghou.Zhinu.Testing;

/// <summary>Reusable smoke checks for custom durable-store implementations.</summary>
public static class WorkflowStoreConformance
{
    public static async Task VerifyRunRoundTripAsync(
        IWorkflowStore store,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        await store.InitializeAsync(cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var run = new WorkflowRun
        {
            Id = Guid.NewGuid(),
            WorkflowName = "conformance",
            WorkflowVersion = "1",
            Status = WorkflowStatus.Pending,
            CreatedAt = now,
            UpdatedAt = now,
            InputJson = "null",
            InputType = typeof(object).FullName,
            OutputType = typeof(object).FullName
        };
        await store.CreateRunAsync(run, cancellationToken).ConfigureAwait(false);
        var persisted = await store.GetRunAsync(run.Id, cancellationToken).ConfigureAwait(false);
        if (persisted is null || persisted.WorkflowName != run.WorkflowName)
            throw new InvalidOperationException("The workflow store failed the run round-trip contract.");
    }

    /// <summary>
    /// Verifies durable artifact publication, idempotency, metadata, and lookup
    /// for a custom store implementation.
    /// </summary>
    public static async Task VerifyArtifactRoundTripAsync(
        IWorkflowStore store,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        await store.InitializeAsync(cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var run = new WorkflowRun
        {
            Id = Guid.NewGuid(),
            WorkflowName = "artifact-conformance",
            WorkflowVersion = "1",
            Status = WorkflowStatus.Pending,
            CreatedAt = now,
            UpdatedAt = now
        };
        await store.CreateRunAsync(run, cancellationToken).ConfigureAwait(false);
        var request = new ArtifactPublicationRequest
        {
            WorkflowRunId = run.Id,
            Now = now,
            Artifact = new WorkflowArtifactDescriptor
            {
                Name = "result",
                ArtifactType = "application/octet-stream",
                ArtifactVersion = "1",
                Location = "conformance://artifact/result",
                ContentHash = "sha256:conformance",
                Metadata = new Dictionary<string, string> { ["source"] = "test" }
            }
        };
        var firstPublication = await store.PublishArtifactAsync(request, cancellationToken)
            .ConfigureAwait(false);
        var repeatedPublication = await store.PublishArtifactAsync(request, cancellationToken)
            .ConfigureAwait(false);
        var first = firstPublication.Artifact;
        var repeated = repeatedPublication.Artifact;
        var fetched = await store.GetArtifactAsync(first.Id, cancellationToken)
            .ConfigureAwait(false);
        var listed = await store.GetArtifactsAsync(run.Id, cancellationToken)
            .ConfigureAwait(false);
        var listedArtifact = listed.Count == 1 ? listed[0] : null;
        var hasExpectedMetadata = listedArtifact?.Metadata?.TryGetValue(
            "source", out var source) == true && source == "test";
        if (!firstPublication.Created || firstPublication.Event?.EventType !=
                WorkflowEventTypes.ArtifactPublished || repeatedPublication.Created ||
            first.Id != repeated.Id || fetched?.Id != first.Id ||
            listedArtifact?.ContentHash != request.Artifact.ContentHash ||
            !hasExpectedMetadata)
        {
            throw new InvalidOperationException(
                "The workflow store failed the artifact round-trip contract.");
        }
    }
}
