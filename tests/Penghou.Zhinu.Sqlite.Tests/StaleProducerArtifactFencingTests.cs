using FluentAssertions;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;

namespace Penghou.Zhinu.Sqlite.Tests;

/// <summary>
/// Stale-producer fencing for artifact publication: an obsolete step
/// execution must not become the run's latest artifact. Rejected
/// publications leave no artifact row and no publication event.
/// </summary>
public sealed class StaleProducerArtifactFencingTests : WorkflowEngineTestBase
{
    [Fact]
    public async Task Restart_ObsoleteExecution_PublicationRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var engine = CreateEngine(new PublishingWorkflow(), "fence-artifact-restart");
        var runId = await engine.StartAsync(
            "fence-artifact-restart", "1", "x", cancellationToken: ct);
        await engine.ExecuteAsync(runId, ct);
        await engine.WaitForCompletionAsync<string>(runId, cancellationToken: ct);
        var oldStep = (await engine.GetStepsAsync(runId, ct))
            .Single(item => item.StepKey == "produce");

        await engine.RestartStepAsync(runId, "produce", ct);
        var peer = PeerStore();
        var act = () => peer.PublishArtifactAsync(
            new ArtifactPublicationRequest
            {
                WorkflowRunId = runId,
                StepExecutionId = oldStep.Id,
                ProducerStepKey = oldStep.StepKey,
                ProducerStepRevision = oldStep.Revision,
                ProducerLeaseOwner = "stale-worker",
                Artifact = Artifact("file:///evil"),
                Now = DateTimeOffset.UtcNow
            },
            ct).AsTask();

        (await act.Should().ThrowAsync<LeaseLostException>())
            .WithMessage("*superseded*");
        var latest = await peer.GetLatestArtifactAsync(runId, "result", ct);
        latest.Should().NotBeNull();
        latest!.Location.Should().Be("file:///result");
    }

    [Fact]
    public async Task CompletedStep_LatePublication_Refused()
    {
        var ct = TestContext.Current.CancellationToken;
        var engine = CreateEngine(new PublishingWorkflow(), "fence-artifact-late");
        var runId = await engine.StartAsync(
            "fence-artifact-late", "1", "x", cancellationToken: ct);
        await engine.ExecuteAsync(runId, ct);
        await engine.WaitForCompletionAsync<string>(runId, cancellationToken: ct);
        var step = (await engine.GetStepsAsync(runId, ct))
            .Single(item => item.StepKey == "produce");

        var peer = PeerStore();
        var act = () => peer.PublishArtifactAsync(
            new ArtifactPublicationRequest
            {
                WorkflowRunId = runId,
                StepExecutionId = step.Id,
                ProducerStepKey = step.StepKey,
                ProducerStepRevision = step.Revision,
                ProducerLeaseOwner = "late-worker",
                Artifact = Artifact("file:///evil"),
                Now = DateTimeOffset.UtcNow
            },
            ct).AsTask();

        await act.Should().ThrowAsync<LeaseLostException>();
        (await peer.GetArtifactsAsync(runId, ct))
            .Should().ContainSingle()
            .Which.Location.Should().Be("file:///result");
    }

    [Fact]
    public async Task Restart_CurrentExecution_PublishesNewLatest()
    {
        var ct = TestContext.Current.CancellationToken;
        var engine = CreateEngine(new PublishingWorkflow(), "fence-artifact-current");
        var runId = await engine.StartAsync(
            "fence-artifact-current", "1", "x", cancellationToken: ct);
        await engine.ExecuteAsync(runId, ct);
        await engine.WaitForCompletionAsync<string>(runId, cancellationToken: ct);
        await engine.RestartStepAsync(runId, "produce", ct);
        await engine.ExecuteAsync(runId, ct);
        await engine.WaitForCompletionAsync<string>(runId, cancellationToken: ct);

        var peer = PeerStore();
        var latest = await peer.GetLatestArtifactAsync(runId, "result", ct);
        latest.Should().NotBeNull();
        latest!.Location.Should().Be("file:///result");
        latest.ProducerStepRevision.Should().Be(2);
    }

    private static WorkflowArtifactDescriptor Artifact(string location) => new()
    {
        Name = "result",
        ArtifactType = "text/plain",
        Location = location
    };

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
