using FluentAssertions;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;

namespace Penghou.Zhinu.Sqlite.Tests;

/// <summary>
/// Artifact vs evidence invalidation: tombstoning an artifact revision hides
/// it from latest-revision serving without deleting rows, while evidence
/// invalidation only flags the retained artifact for revalidation. Both emit
/// an observable invalidation event.
/// </summary>
public sealed class ArtifactInvalidationTests : WorkflowEngineTestBase
{
    [Fact]
    public async Task ArtifactTombstone_HidesRevisionFromLatestWithoutDeleting()
    {
        var ct = TestContext.Current.CancellationToken;
        var engine = CreateEngine(new PublishingWorkflow(), "artifact-tombstone");
        var runId = await engine.StartAsync(
            "artifact-tombstone", "1", "x", cancellationToken: ct);
        await engine.ExecuteAsync(runId, ct);
        await engine.WaitForCompletionAsync<string>(runId, cancellationToken: ct);
        await engine.RestartStepAsync(runId, "produce", ct);
        await engine.ExecuteAsync(runId, ct);
        await engine.WaitForCompletionAsync<string>(runId, cancellationToken: ct);
        var store = PeerStore();
        var second = (await store.GetLatestArtifactAsync(runId, "result", ct))!;
        second.ProducerStepRevision.Should().Be(2);

        var invalidation = await store.InvalidateArtifactAsync(
            second.Id, ArtifactInvalidationKind.Artifact, "wrong content", "reviewer",
            DateTimeOffset.UtcNow, ct);

        invalidation.ArtifactId.Should().Be(second.Id);
        invalidation.Kind.Should().Be(ArtifactInvalidationKind.Artifact);
        invalidation.Reason.Should().Be("wrong content");
        var latest = await store.GetLatestArtifactAsync(runId, "result", ct);
        latest.Should().NotBeNull();
        latest!.ProducerStepRevision.Should().Be(1);
        // Rows are retained for audit.
        (await store.GetArtifactAsync(second.Id, ct)).Should().NotBeNull();
        (await store.GetArtifactsAsync(runId, ct)).Should().HaveCount(2);
        (await store.GetInvalidationsAsync(second.Id, ct))
            .Should().ContainSingle()
            .Which.InvalidationId.Should().Be(invalidation.InvalidationId);
        (await store.GetEventsAsync(runId, 0, 100, ct)).Should()
            .ContainSingle(item => item.EventType == WorkflowEventTypes.ArtifactInvalidated);
    }

    [Fact]
    public async Task TombstoningAllRevisions_ServesNoLatest()
    {
        var ct = TestContext.Current.CancellationToken;
        var engine = CreateEngine(new PublishingWorkflow(), "artifact-tombstone-all");
        var runId = await engine.StartAsync(
            "artifact-tombstone-all", "1", "x", cancellationToken: ct);
        await engine.ExecuteAsync(runId, ct);
        await engine.WaitForCompletionAsync<string>(runId, cancellationToken: ct);
        var store = PeerStore();
        var first = (await store.GetLatestArtifactAsync(runId, "result", ct))!;

        await store.InvalidateArtifactAsync(
            first.Id, ArtifactInvalidationKind.Artifact, null, null,
            DateTimeOffset.UtcNow, ct);

        (await store.GetLatestArtifactAsync(runId, "result", ct)).Should().BeNull();
    }

    [Fact]
    public async Task EvidenceInvalidation_RetainsServingForRevalidation()
    {
        var ct = TestContext.Current.CancellationToken;
        var engine = CreateEngine(new PublishingWorkflow(), "artifact-evidence");
        var runId = await engine.StartAsync(
            "artifact-evidence", "1", "x", cancellationToken: ct);
        await engine.ExecuteAsync(runId, ct);
        await engine.WaitForCompletionAsync<string>(runId, cancellationToken: ct);
        var store = PeerStore();
        var published = (await store.GetLatestArtifactAsync(runId, "result", ct))!;

        var invalidation = await store.InvalidateArtifactAsync(
            published.Id, ArtifactInvalidationKind.Evidence, "stricter criteria", "qa",
            DateTimeOffset.UtcNow, ct);

        invalidation.Kind.Should().Be(ArtifactInvalidationKind.Evidence);
        var latest = await store.GetLatestArtifactAsync(runId, "result", ct);
        latest.Should().NotBeNull();
        latest!.Id.Should().Be(published.Id);
        (await store.GetInvalidationsAsync(published.Id, ct))
            .Should().ContainSingle()
            .Which.Reason.Should().Be("stricter criteria");
    }

    [Fact]
    public async Task Invalidations_AreScopedPerArtifactRevision()
    {
        var ct = TestContext.Current.CancellationToken;
        var engine = CreateEngine(new PublishingWorkflow(), "artifact-invalidation-scope");
        var runId = await engine.StartAsync(
            "artifact-invalidation-scope", "1", "x", cancellationToken: ct);
        await engine.ExecuteAsync(runId, ct);
        await engine.WaitForCompletionAsync<string>(runId, cancellationToken: ct);
        await engine.RestartStepAsync(runId, "produce", ct);
        await engine.ExecuteAsync(runId, ct);
        await engine.WaitForCompletionAsync<string>(runId, cancellationToken: ct);
        var store = PeerStore();
        var revisions = await store.GetArtifactsAsync(runId, ct);
        var first = revisions.Single(item => item.ProducerStepRevision == 1);
        var second = revisions.Single(item => item.ProducerStepRevision == 2);

        await store.InvalidateArtifactAsync(
            first.Id, ArtifactInvalidationKind.Evidence, null, null,
            DateTimeOffset.UtcNow, ct);

        (await store.GetInvalidationsAsync(second.Id, ct)).Should().BeEmpty();
        (await store.GetLatestArtifactAsync(runId, "result", ct))!.Id
            .Should().Be(second.Id);
    }

    [Fact]
    public async Task Invalidate_MissingArtifact_FailsWithNotFound()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = PeerStore();

        var act = () => store.InvalidateArtifactAsync(
            Guid.NewGuid(), ArtifactInvalidationKind.Artifact, null, null,
            DateTimeOffset.UtcNow, ct).AsTask();

        await act.Should().ThrowAsync<WorkflowNotFoundException>();
        var badKind = () => store.InvalidateArtifactAsync(
            Guid.NewGuid(), (ArtifactInvalidationKind)99, null, null,
            DateTimeOffset.UtcNow, ct).AsTask();
        await badKind.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    private SqliteWorkflowStore PeerStore() =>
        new(new ZhinuSqliteOptions
        {
            DatabasePath = Path.Combine(root, "zhinu.db"),
            BusyTimeout = TimeSpan.FromSeconds(2),
            Pooling = false
        });

    private sealed class PublishingWorkflow : IWorkflow<string, string>
    {
        public Task<string> RunAsync(
            WorkflowContext context, string input, CancellationToken cancellationToken) =>
            context.StepAsync(
                "produce",
                async (step, token) =>
                {
                    await step.PublishArtifactAsync(
                        new WorkflowArtifactDescriptor
                        {
                            Name = "result",
                            ArtifactType = "text/plain",
                            Location = "file:///result"
                        },
                        token);
                    return input;
                },
                cancellationToken: cancellationToken);
    }
}
