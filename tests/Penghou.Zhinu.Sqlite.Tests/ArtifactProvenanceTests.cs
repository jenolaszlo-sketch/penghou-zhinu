using FluentAssertions;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;

namespace Penghou.Zhinu.Sqlite.Tests;

/// <summary>
/// Artifact production provenance: step-scoped publication binds the
/// producing step revision's effective inputs hash and implementation key
/// into the artifact record, following the fenced revision across restarts.
/// Run-scoped publications carry no producer provenance.
/// </summary>
public sealed class ArtifactProvenanceTests : WorkflowEngineTestBase
{
    [Fact]
    public async Task StepPublication_BindsFencedProducerProvenance()
    {
        var ct = TestContext.Current.CancellationToken;
        var engine = CreateEngine(new PublishingWorkflow(), "artifact-provenance");
        var runId = await engine.StartAsync(
            "artifact-provenance", "1", "x", cancellationToken: ct);
        await engine.ExecuteAsync(runId, ct);
        await engine.WaitForCompletionAsync<string>(runId, cancellationToken: ct);

        var step = (await engine.GetStepsAsync(runId, ct))
            .Single(item => item.StepKey == "produce");
        var artifact = (await engine.GetArtifactsAsync(runId, ct))
            .Should().ContainSingle().Subject;

        artifact.ProducerStepKey.Should().Be("produce");
        artifact.ProducerStepRevision.Should().Be(step.Revision);
        artifact.EffectiveInputsHash.Should().Be(step.InputHash);
        artifact.EffectiveInputsHash.Should().NotBeNull();
        // Functional steps carry no implementation key; class-based steps do.
        artifact.ProducerSemantics.Should().Be(step.ImplementationKey);
        (await engine.GetArtifactAsync(artifact.Id, ct)).Should().BeEquivalentTo(artifact);
        (await engine.GetLatestArtifactAsync(runId, "result", ct))
            .Should().BeEquivalentTo(artifact);
    }

    [Fact]
    public async Task RestartedProducer_BindsNewRevisionProvenance()
    {
        var ct = TestContext.Current.CancellationToken;
        var engine = CreateEngine(new PublishingWorkflow(), "artifact-provenance-restart");
        var runId = await engine.StartAsync(
            "artifact-provenance-restart", "1", "x", cancellationToken: ct);
        await engine.ExecuteAsync(runId, ct);
        await engine.WaitForCompletionAsync<string>(runId, cancellationToken: ct);

        await engine.RestartStepAsync(runId, "produce", ct);
        await engine.ExecuteAsync(runId, ct);
        await engine.WaitForCompletionAsync<string>(runId, cancellationToken: ct);

        var step = (await engine.GetStepsAsync(runId, ct))
            .Single(item => item.StepKey == "produce" && item.Revision == 2);
        var latest = await engine.GetLatestArtifactAsync(runId, "result", ct);

        latest.Should().NotBeNull();
        latest!.ProducerStepRevision.Should().Be(2);
        latest.EffectiveInputsHash.Should().Be(step.InputHash);
        latest.ProducerSemantics.Should().Be(step.ImplementationKey);
    }

    [Fact]
    public async Task RunScopedPublication_HasNoProducerProvenance()
    {
        var ct = TestContext.Current.CancellationToken;
        var engine = CreateEngine(new PublishingWorkflow(), "artifact-provenance-run");
        var runId = await engine.StartAsync(
            "artifact-provenance-run", "1", "x", cancellationToken: ct);
        var store = PeerStore();

        var result = await store.PublishArtifactAsync(
            new ArtifactPublicationRequest
            {
                WorkflowRunId = runId,
                Artifact = new WorkflowArtifactDescriptor
                {
                    Name = "run-note",
                    ArtifactType = "text/plain",
                    Location = "file:///run-note"
                },
                Now = DateTimeOffset.UtcNow
            },
            ct);

        result.Created.Should().BeTrue();
        result.Artifact.EffectiveInputsHash.Should().BeNull();
        result.Artifact.ProducerSemantics.Should().BeNull();
        result.Artifact.ProducerStepKey.Should().BeNull();
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
