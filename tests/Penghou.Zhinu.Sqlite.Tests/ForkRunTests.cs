using FluentAssertions;

namespace Penghou.Zhinu.Sqlite.Tests;

public sealed class ForkRunTests : WorkflowEngineTestBase
{
    [Fact]
    public async Task ForkAsync_ReusesCompletedPrefixAndIndependentBranch()
    {
        var sourceWorkflow = new DependentStepsWorkflow();
        var engine = CreateEngine(sourceWorkflow, "fork-deps");
        var sourceResult = await engine.RunAsync<string, string>(
            "fork-deps",
            "1",
            "x",
            cancellationToken: TestContext.Current.CancellationToken);
        sourceResult.Should().Be("D[B[A(x)]]|E[C(x)]");

        var preview = await engine.PlanForkAsync(
            sourceWorkflow.RunId,
            "b",
            cancellationToken: TestContext.Current.CancellationToken);
        preview.StepsToReuse.Should().Equal("a", "c", "e");
        preview.StepsToReexecute.Should().Contain(item =>
            item.StepKey == "b" && item.Reason == ForkStepReason.Requested);
        preview.StepsToReexecute.Should().Contain(item =>
            item.StepKey == "d" && item.Reason == ForkStepReason.Dependent);

        var forkId = Guid.NewGuid();
        var createdId = await engine.ForkAsync(
            sourceWorkflow.RunId,
            "b",
            new ForkRunOptions
            {
                WorkflowRunId = forkId,
                Actor = "test",
                Reason = "try another implementation"
            },
            TestContext.Current.CancellationToken);
        createdId.Should().Be(forkId);

        var forkWorkflow = new DependentStepsWorkflow();
        var forkEngine = CreateEngine(forkWorkflow, "fork-deps");
        await forkEngine.ExecuteAsync(
            forkId,
            TestContext.Current.CancellationToken);
        var result = await forkEngine.WaitForCompletionAsync<string>(
            forkId,
            cancellationToken: TestContext.Current.CancellationToken);

        result.Should().Be(sourceResult);
        forkWorkflow.ACalls.Should().Be(0);
        forkWorkflow.BCalls.Should().Be(1);
        forkWorkflow.CCalls.Should().Be(0);
        forkWorkflow.DCalls.Should().Be(1);
        forkWorkflow.ECalls.Should().Be(0);
        var fork = await forkEngine.GetRunAsync(
            forkId,
            TestContext.Current.CancellationToken);
        fork!.SourceRunId.Should().Be(sourceWorkflow.RunId);
        fork.ParentRunId.Should().BeNull();
        var source = await forkEngine.GetRunAsync(
            sourceWorkflow.RunId,
            TestContext.Current.CancellationToken);
        source!.Status.Should().Be(WorkflowStatus.Completed);
        source.SourceRunId.Should().BeNull();
        (await forkEngine.GetEventsAsync(
            forkId,
            cancellationToken: TestContext.Current.CancellationToken)).Should()
            .Contain(item => item.EventType == WorkflowEventTypes.RunForked);
    }

    [Fact]
    public async Task ForkAsync_CreationOrder_ReexecutesSelectedStepAndLaterSteps()
    {
        var sourceWorkflow = new DependentStepsWorkflow();
        var engine = CreateEngine(sourceWorkflow, "fork-order");
        await engine.RunAsync<string, string>(
            "fork-order",
            "1",
            "x",
            cancellationToken: TestContext.Current.CancellationToken);

        var preview = await engine.PlanForkAsync(
            sourceWorkflow.RunId,
            "b",
            StepRestartMode.CreationOrder,
            TestContext.Current.CancellationToken);

        preview.StepsToReuse.Should().Equal("a", "c");
        preview.StepsToReexecute.Select(item => item.StepKey)
            .Should().Equal("b", "d", "e");
    }

    [Fact]
    public async Task ForkAsync_RejectsExistingDestinationWithoutChangingIt()
    {
        var sourceWorkflow = new TwoStepWorkflow();
        var engine = CreateEngine(sourceWorkflow, "fork-existing");
        var sourceId = await engine.StartAsync(
            "fork-existing",
            "1",
            "source",
            cancellationToken: TestContext.Current.CancellationToken);
        await engine.ExecuteAsync(sourceId, TestContext.Current.CancellationToken);
        var existingId = await engine.StartAsync(
            "fork-existing",
            "1",
            "existing",
            cancellationToken: TestContext.Current.CancellationToken);

        await engine.Invoking(current => current.ForkAsync(
                sourceId,
                "one",
                new ForkRunOptions { WorkflowRunId = existingId },
                TestContext.Current.CancellationToken))
            .Should().ThrowAsync<WorkflowStateException>();

        var existing = await engine.GetRunAsync(
            existingId,
            TestContext.Current.CancellationToken);
        existing!.InputJson.Should().Contain("existing");
        (await engine.GetStepsAsync(
            existingId,
            TestContext.Current.CancellationToken)).Should().BeEmpty();
    }

    [Fact]
    public async Task ForkAsync_FromFailedStep_ReusesCommittedPrefix()
    {
        var sourceWorkflow = new ForkFailureWorkflow(failSecond: true);
        var engine = CreateEngine(sourceWorkflow, "fork-failure");
        var sourceId = await engine.StartAsync(
            "fork-failure",
            "1",
            "x",
            cancellationToken: TestContext.Current.CancellationToken);
        await engine.ExecuteAsync(
            sourceId,
            TestContext.Current.CancellationToken);
        (await engine.GetRunAsync(
            sourceId,
            TestContext.Current.CancellationToken))!.Status.Should()
            .Be(WorkflowStatus.Failed);

        var preview = await engine.PlanForkAsync(
            sourceId,
            "second",
            cancellationToken: TestContext.Current.CancellationToken);
        preview.StepsToReuse.Should().Equal("first");
        preview.StepsToReexecute.Should().ContainSingle().Which.Should()
            .Be(new ForkPlanStep("second", ForkStepReason.Requested));

        var forkId = await engine.ForkAsync(
            sourceId,
            "second",
            cancellationToken: TestContext.Current.CancellationToken);
        var resumed = new ForkFailureWorkflow(failSecond: false);
        var resumedEngine = CreateEngine(resumed, "fork-failure");
        await resumedEngine.ExecuteAsync(
            forkId,
            TestContext.Current.CancellationToken);

        (await resumedEngine.WaitForCompletionAsync<string>(
            forkId,
            cancellationToken: TestContext.Current.CancellationToken)).Should()
            .Be("second:first:x");
        resumed.FirstCalls.Should().Be(0);
        resumed.SecondCalls.Should().Be(1);
    }

    private sealed class ForkFailureWorkflow(bool failSecond) :
        IWorkflow<string, string>
    {
        public int FirstCalls;
        public int SecondCalls;

        public async Task<string> RunAsync(
            WorkflowContext context,
            string input,
            CancellationToken cancellationToken)
        {
            var first = await context.StepAsync(
                "first",
                input,
                (value, _) =>
                {
                    FirstCalls++;
                    return Task.FromResult($"first:{value}");
                },
                cancellationToken: cancellationToken);
            using (context.DependsOn("first"))
            {
                return await context.StepAsync(
                    "second",
                    first,
                    (value, _) =>
                    {
                        SecondCalls++;
                        return failSecond
                            ? throw new InvalidOperationException("expected")
                            : Task.FromResult($"second:{value}");
                    },
                    cancellationToken: cancellationToken);
            }
        }
    }
}
