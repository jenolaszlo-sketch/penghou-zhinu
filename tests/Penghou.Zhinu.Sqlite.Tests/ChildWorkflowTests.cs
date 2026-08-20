using FluentAssertions;

namespace Penghou.Zhinu.Sqlite.Tests;

public sealed class ChildWorkflowTests : WorkflowEngineTestBase
{

    [Fact]
    public async Task StartChildAsync_ExecutesChildAndWaitsForResult()
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
        var result = await engine.WaitForCompletionAsync<string>(
            runId,
            cancellationToken: TestContext.Current.CancellationToken);

        result.Should().Be("child:parent:go");
        child.Calls.Should().Be(1);
        var childRun = (await engine.GetRunsAsync(
            new RunQuery { WorkflowName = "child" },
            cancellationToken: TestContext.Current.CancellationToken)).Single();
        childRun.Status.Should().Be(WorkflowStatus.Completed);
        childRun.ParentRunId.Should().Be(runId);
        (await engine.GetStepsAsync(
            runId,
            TestContext.Current.CancellationToken)).Should().HaveCount(3)
            .And.OnlyContain(step => step.Status == StepStatus.Completed);
    }

    [Fact]
    public async Task StartChildAsync_ChildFailure_PropagatesToParent()
    {
        var parent = new FailingParentWorkflow();
        var child = new FailingChildWorkflow();
        var engine = CreateEngine(
            new WorkflowRegistry()
                .Register("fail-parent", "1", parent)
                .Register("bad-child", "1", child));
        var runId = await engine.StartAsync(
            "fail-parent",
            "1",
            "x",
            cancellationToken: TestContext.Current.CancellationToken);

        await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);

        var action = () => engine.WaitForCompletionAsync<string>(
            runId,
            cancellationToken: TestContext.Current.CancellationToken);
        await action.Should().ThrowAsync<WorkflowExecutionFailedException>()
            .WithMessage("*child failed*");
        (await engine.GetRunsAsync(
            new RunQuery { WorkflowName = "bad-child" },
            cancellationToken: TestContext.Current.CancellationToken)).Single()
            .Status.Should().Be(WorkflowStatus.Failed);
        (await engine.GetRunAsync(
            runId,
            TestContext.Current.CancellationToken))!.Status.Should().Be(WorkflowStatus.Failed);
    }

    [Fact]
    public async Task StartChildAsync_RestartingStartStepReusesExistingChildRun()
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
        var original = await engine.WaitForCompletionAsync<string>(
            runId,
            cancellationToken: TestContext.Current.CancellationToken);
        original.Should().Be("child:parent:go");

        await engine.RestartStepAsync(
            runId,
            "child:start",
            TestContext.Current.CancellationToken);
        await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
        var rerun = await engine.WaitForCompletionAsync<string>(
            runId,
            cancellationToken: TestContext.Current.CancellationToken);
        rerun.Should().Be("child:parent:go");
        var runs = await engine.GetRunsAsync(
            new RunQuery { WorkflowName = "child" },
            cancellationToken: TestContext.Current.CancellationToken);
        runs.Should().ContainSingle()
            .Which.ParentRunId.Should().Be(runId);
    }

    [Fact]
    public async Task StartChildAsync_RestartWithChangedInputRejectsExistingChildRun()
    {
        var firstEngine = CreateEngine(
            new WorkflowRegistry()
                .Register("mutable-parent", "1", new MutableChildInputParentWorkflow())
                .Register("child", "1", new ChildWorkflow()));
        var runId = await firstEngine.StartAsync(
            "mutable-parent",
            "1",
            "go",
            cancellationToken: TestContext.Current.CancellationToken);
        await firstEngine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
        await firstEngine.WaitForCompletionAsync<string>(
            runId,
            cancellationToken: TestContext.Current.CancellationToken);

        await firstEngine.RestartStepAsync(
            runId,
            "parent-step",
            TestContext.Current.CancellationToken);
        var changedEngine = CreateEngine(
            new WorkflowRegistry()
                .Register(
                    "mutable-parent",
                    "1",
                    new MutableChildInputParentWorkflow { Suffix = "b" })
                .Register("child", "1", new ChildWorkflow()));
        await changedEngine.ExecuteAsync(runId, TestContext.Current.CancellationToken);

        var action = () => changedEngine.WaitForCompletionAsync<string>(
            runId,
            cancellationToken: TestContext.Current.CancellationToken);
        await action.Should().ThrowAsync<WorkflowExecutionFailedException>()
            .WithMessage("*incompatible input or result contract*");
    }

    [Fact]
    public async Task StartChildAsync_RecordsWaitToStartDependencyEdge()
    {
        var parent = new ParentWorkflow();
        var child = new ChildWorkflow();
        var engine = CreateEngine(
            new WorkflowRegistry()
                .Register("parent-edge", "1", parent)
                .Register("child", "1", child));
        var runId = await engine.StartAsync(
            "parent-edge",
            "1",
            "go",
            cancellationToken: TestContext.Current.CancellationToken);
        await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
        await engine.WaitForCompletionAsync<string>(
            runId,
            cancellationToken: TestContext.Current.CancellationToken);

        var graph = await engine.GetDependencyGraphAsync(
            runId,
            TestContext.Current.CancellationToken);
        graph.Should().Contain(item =>
            item.StepKey == "child:wait" && item.DependsOnStepKey == "child:start");

        var plan = await engine.RestartStepAsync(
            runId,
            "child:start",
            cancellationToken: TestContext.Current.CancellationToken);
        plan.StepsToInvalidate.Select(item => item.StepKey)
            .Should().Equal("child:start", "child:wait");

        await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
        var rerun = await engine.WaitForCompletionAsync<string>(
            runId,
            cancellationToken: TestContext.Current.CancellationToken);
        rerun.Should().Be("child:parent:go");
        (await engine.GetRunsAsync(
            new RunQuery { WorkflowName = "child" },
            cancellationToken: TestContext.Current.CancellationToken))
            .Should().ContainSingle();
    }
}
