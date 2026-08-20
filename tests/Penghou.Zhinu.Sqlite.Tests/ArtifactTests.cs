using FluentAssertions;

namespace Penghou.Zhinu.Sqlite.Tests;

public sealed class ArtifactTests : WorkflowEngineTestBase
{
    [Fact]
    public async Task StepArtifact_IsInspectableAfterWorkflowFailure()
    {
        var workflow = new ArtifactThenFailWorkflow();
        var engine = CreateEngine(workflow, "artifact-failure");

        var action = () => engine.RunAsync<string, string>(
            "artifact-failure",
            "1",
            "input",
            cancellationToken: TestContext.Current.CancellationToken);
        await action.Should().ThrowAsync<WorkflowExecutionFailedException>();

        var artifacts = await engine.GetArtifactsAsync(
            workflow.RunId,
            TestContext.Current.CancellationToken);
        var artifact = artifacts.Should().ContainSingle().Subject;
        artifact.Name.Should().Be("plan");
        artifact.Revision.Should().Be(1);
        artifact.ArtifactType.Should().Be("application/json");
        artifact.ArtifactVersion.Should().Be("1");
        artifact.Location.Should().Be("file:///workspace/plan.json");
        artifact.ContentHash.Should().Be("sha256:123");
        artifact.Metadata.Should().Contain("stage", "planning");
        artifact.ProducerStepKey.Should().Be("planning");
        artifact.ProducerStepRevision.Should().Be(1);

        (await engine.GetArtifactAsync(
            artifact.Id,
            TestContext.Current.CancellationToken)).Should().BeEquivalentTo(artifact);
    }

    [Fact]
    public async Task RepeatedIdenticalPublication_InOneStepRevision_IsIdempotent()
    {
        var workflow = new DuplicateArtifactWorkflow(conflict: false);
        var engine = CreateEngine(workflow, "artifact-idempotent");

        await engine.RunAsync<string, string>(
            "artifact-idempotent",
            "1",
            "input",
            cancellationToken: TestContext.Current.CancellationToken);

        workflow.First.Should().Be(workflow.Second);
        (await engine.GetArtifactsAsync(
            workflow.RunId,
            TestContext.Current.CancellationToken)).Should().ContainSingle();
    }

    [Fact]
    public async Task ConflictingPublication_InOneStepRevision_FailsStep()
    {
        var workflow = new DuplicateArtifactWorkflow(conflict: true);
        var engine = CreateEngine(workflow, "artifact-conflict");

        var action = () => engine.RunAsync<string, string>(
            "artifact-conflict",
            "1",
            "input",
            cancellationToken: TestContext.Current.CancellationToken);
        await action.Should().ThrowAsync<WorkflowExecutionFailedException>();

        var artifacts = await engine.GetArtifactsAsync(
            workflow.RunId,
            TestContext.Current.CancellationToken);
        artifacts.Should().ContainSingle().Which.Location.Should().Be("file:///one");
    }

    [Fact]
    public async Task RestartedProducer_CreatesNextArtifactRevision()
    {
        var workflow = new RevisableArtifactWorkflow();
        var engine = CreateEngine(workflow, "artifact-revision");
        var runId = await engine.StartAsync(
            "artifact-revision",
            "1",
            "input",
            cancellationToken: TestContext.Current.CancellationToken);
        await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);

        workflow.Location = "file:///two";
        await engine.RestartStepAsync(
            runId,
            "produce",
            cancellationToken: TestContext.Current.CancellationToken);
        await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);

        var artifacts = await engine.GetArtifactsAsync(
            runId,
            TestContext.Current.CancellationToken);
        artifacts.Select(item => item.Revision).Should().Equal(1, 2);
        artifacts.Select(item => item.ProducerStepRevision).Should().Equal(1, 2);
        artifacts.Select(item => item.Location).Should().Equal("file:///one", "file:///two");
    }

    [Fact]
    public async Task ForkedWorkflow_CanChainReusedArtifactReference()
    {
        var sourceWorkflow = new ArtifactChainWorkflow();
        var engine = CreateEngine(sourceWorkflow, "artifact-fork");
        await engine.RunAsync<string, string>(
            "artifact-fork",
            "1",
            "input",
            cancellationToken: TestContext.Current.CancellationToken);

        var forkId = await engine.ForkAsync(
            sourceWorkflow.RunId,
            "consume",
            cancellationToken: TestContext.Current.CancellationToken);
        var forkWorkflow = new ArtifactChainWorkflow();
        var forkEngine = CreateEngine(forkWorkflow, "artifact-fork");
        await forkEngine.ExecuteAsync(forkId, TestContext.Current.CancellationToken);
        var result = await forkEngine.WaitForCompletionAsync<string>(
            forkId,
            cancellationToken: TestContext.Current.CancellationToken);

        result.Should().Be("file:///source");
        forkWorkflow.PublishCalls.Should().Be(0);
        forkWorkflow.ConsumedArtifact!.WorkflowRunId.Should().Be(sourceWorkflow.RunId);
        (await forkEngine.GetArtifactAsync(
            forkWorkflow.ConsumedArtifact.Id,
            TestContext.Current.CancellationToken)).Should().NotBeNull();
    }

    private sealed class ArtifactThenFailWorkflow : IWorkflow<string, string>
    {
        public Guid RunId { get; private set; }

        public async Task<string> RunAsync(
            WorkflowContext context,
            string input,
            CancellationToken cancellationToken)
        {
            RunId = context.WorkflowRunId;
            return await context.StepAsync<string>(
                "planning",
                async (step, token) =>
                {
                    await step.PublishArtifactAsync(
                        new WorkflowArtifactDescriptor
                        {
                            Name = "plan",
                            ArtifactType = "application/json",
                            ArtifactVersion = "1",
                            Location = "file:///workspace/plan.json",
                            ContentHash = "sha256:123",
                            Metadata = new Dictionary<string, string>
                            {
                                ["stage"] = "planning"
                            }
                        },
                        token);
                    throw new InvalidOperationException("planning failed");
                },
                new StepOptions { Retry = new RetryPolicy { MaxAttempts = 1 } },
                cancellationToken);
        }
    }

    private sealed class DuplicateArtifactWorkflow(bool conflict) :
        IWorkflow<string, string>
    {
        public Guid RunId { get; private set; }
        public WorkflowArtifactReference? First { get; private set; }
        public WorkflowArtifactReference? Second { get; private set; }

        public async Task<string> RunAsync(
            WorkflowContext context,
            string input,
            CancellationToken cancellationToken)
        {
            RunId = context.WorkflowRunId;
            return await context.StepAsync(
                "produce",
                async (step, token) =>
                {
                    First = await step.PublishArtifactAsync(
                        Artifact("file:///one"), token);
                    Second = await step.PublishArtifactAsync(
                        Artifact(conflict ? "file:///two" : "file:///one"), token);
                    return input;
                },
                new StepOptions { Retry = new RetryPolicy { MaxAttempts = 1 } },
                cancellationToken);
        }

        private static WorkflowArtifactDescriptor Artifact(string location) => new()
        {
            Name = "result",
            ArtifactType = "text/plain",
            Location = location
        };
    }

    private sealed class RevisableArtifactWorkflow : IWorkflow<string, string>
    {
        public string Location { get; set; } = "file:///one";

        public Task<string> RunAsync(
            WorkflowContext context,
            string input,
            CancellationToken cancellationToken) =>
            context.StepAsync(
                "produce",
                async (step, token) =>
                {
                    var artifact = await step.PublishArtifactAsync(
                        new WorkflowArtifactDescriptor
                        {
                            Name = "result",
                            ArtifactType = "text/plain",
                            Location = Location
                        },
                        token);
                    return artifact.Location;
                },
                cancellationToken: cancellationToken);
    }

    private sealed class ArtifactChainWorkflow : IWorkflow<string, string>
    {
        public Guid RunId { get; private set; }
        public int PublishCalls { get; private set; }
        public WorkflowArtifactReference? ConsumedArtifact { get; private set; }

        public async Task<string> RunAsync(
            WorkflowContext context,
            string input,
            CancellationToken cancellationToken)
        {
            RunId = context.WorkflowRunId;
            var artifact = await context.StepAsync(
                "produce",
                async (step, token) =>
                {
                    PublishCalls++;
                    return await step.PublishArtifactAsync(
                        new WorkflowArtifactDescriptor
                        {
                            Name = "source",
                            ArtifactType = "text/plain",
                            Location = "file:///source"
                        },
                        token);
                },
                cancellationToken: cancellationToken);
            return await context.StepAsync(
                "consume",
                artifact,
                (value, _) =>
                {
                    ConsumedArtifact = value;
                    return Task.FromResult(value.Location);
                },
                new StepOptions { DependsOn = ["produce"] },
                cancellationToken);
        }
    }
}
