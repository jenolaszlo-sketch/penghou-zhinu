using FluentAssertions;

namespace Penghou.Zhinu.Sqlite.Tests;

public sealed class RunProgressTests : WorkflowEngineTestBase
{

    [Fact]
    public async Task GetRunProgressAsync_ReturnsSnapshotOfParentAndChildRuns()
    {
        var parent = new ParentWorkflow();
        var child = new ChildWorkflow();
        var engine = CreateEngine(
            new WorkflowRegistry()
                .Register("parent", "1", parent)
                .Register("child", "1", child));
        var runId = await engine.StartAsync(
            "parent",
            "1",
            "go",
            cancellationToken: TestContext.Current.CancellationToken);
        await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
        await engine.WaitForCompletionAsync<string>(
            runId,
            cancellationToken: TestContext.Current.CancellationToken);

        var progress = await engine.GetRunProgressAsync(
            runId,
            cancellationToken: TestContext.Current.CancellationToken);

        progress.Should().NotBeNull();
        progress!.Run.Id.Should().Be(runId);
        progress.Run.Status.Should().Be(WorkflowStatus.Completed);
        progress.Steps.Should().HaveCount(3);
        progress.CompletedSteps.Should().Be(3);
        progress.RunningSteps.Should().Be(0);
        progress.WaitingSteps.Should().Be(0);
        progress.FailedSteps.Should().Be(0);
        progress.ExecutedStepKeys.Should().Equal(
            "parent-step", "child:start", "child:wait");
        progress.Events.Should().NotBeEmpty();
        progress.Children.Should().ContainSingle();
        var childProgress = progress.Children[0];
        childProgress.Run.ParentRunId.Should().Be(runId);
        childProgress.Run.Status.Should().Be(WorkflowStatus.Completed);
        childProgress.Steps.Should().ContainSingle()
            .Which.StepKey.Should().Be("child-step");
        childProgress.CompletedSteps.Should().Be(1);
        childProgress.ExecutedStepKeys.Should().Equal("child-step");
        childProgress.Events.Should().NotBeEmpty();
        childProgress.Children.Should().BeEmpty();
    }

    [Fact]
    public async Task GetRunProgressAsync_UnknownRun_ReturnsNull()
    {
        var engine = CreateEngine(new TwoStepWorkflow(), "progress-unknown");
        var progress = await engine.GetRunProgressAsync(
            Guid.NewGuid(),
            cancellationToken: TestContext.Current.CancellationToken);
        progress.Should().BeNull();
    }

    [Fact]
    public async Task GetRunProgressAsync_IncludeEventsFalse_OmitsEvents()
    {
        var engine = CreateEngine(new TwoStepWorkflow(), "progress-no-events");
        var runId = await engine.StartAsync(
            "progress-no-events",
            "1",
            "value",
            cancellationToken: TestContext.Current.CancellationToken);
        await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
        await engine.WaitForCompletionAsync<string>(
            runId,
            cancellationToken: TestContext.Current.CancellationToken);

        var progress = await engine.GetRunProgressAsync(
            runId,
            new RunProgressOptions { IncludeEvents = false },
            cancellationToken: TestContext.Current.CancellationToken);

        progress!.Events.Should().BeEmpty();
        progress.Steps.Should().HaveCount(2);
        progress.CompletedSteps.Should().Be(2);
    }

    [Fact]
    public async Task GetRunProgressAsync_IncludesArtifactsOperationAndDiagnosisShape()
    {
        var workflow = new ProgressArtifactWorkflow();
        var engine = CreateEngine(workflow, "progress-artifact");
        var runId = await engine.StartAsync(
            "progress-artifact",
            "1",
            "value",
            cancellationToken: TestContext.Current.CancellationToken);
        await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);

        var progress = await engine.GetRunProgressAsync(
            runId,
            cancellationToken: TestContext.Current.CancellationToken);

        progress!.Artifacts.Should().ContainSingle().Which.Name.Should().Be("progress-output");
        progress.Diagnosis!.Code.Should().Be(RunDiagnosisCode.Terminal);
        progress.ActiveOperation.Should().BeNull();
        progress.SourceRun.Should().BeNull();
    }

    private sealed class ProgressArtifactWorkflow : IWorkflow<string, string>
    {
        public async Task<string> RunAsync(
            WorkflowContext context,
            string input,
            CancellationToken cancellationToken)
        {
            await context.PublishArtifactAsync(
                new WorkflowArtifactDescriptor
                {
                    Name = "progress-output",
                    ArtifactType = "text/plain",
                    Location = "file:///progress"
                },
                cancellationToken);
            return input;
        }
    }
}
