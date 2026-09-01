using FluentAssertions;

namespace Penghou.Zhinu.Sqlite.Tests;

public sealed class DurableLoopAdministrationTests : WorkflowEngineTestBase
{
    [Fact]
    public void References_DescribeNestedSemanticBoundariesWithoutStorageKeys()
    {
        var outer = WorkflowLoopReference.Root("outer");
        var inner = outer.Iteration(2).NestedLoop("inner");
        var target = inner.Iteration(3).BodyStep("advance");

        inner.Should().Be(
            WorkflowLoopReference.Root("outer").Iteration(2).NestedLoop("inner"));
        inner.Depth.Should().Be(2);
        inner.DisplayPath.Should().Be("outer[2].inner");
        target.Kind.Should().Be(WorkflowLoopStepKind.Body);
        target.BodyStepName.Should().Be("advance");
        target.DisplayPath.Should().Be("outer[2].inner[3].body.advance");
        target.DisplayPath.Should().NotContain("$loop/");
    }

    [Fact]
    public async Task GetLoopProgressAsync_GroupsRootAndNestedBoundaries()
    {
        var workflow = new InspectableNestedLoopWorkflow();
        var engine = CreateEngine(workflow, "loop-progress");
        await engine.RunAsync<string, string>(
            "loop-progress",
            "1",
            "ignored",
            cancellationToken: TestContext.Current.CancellationToken);

        var outer = WorkflowLoopReference.Root("outer");
        var outerProgress = await engine.GetLoopProgressAsync(
            workflow.RunId,
            outer,
            TestContext.Current.CancellationToken);

        outerProgress.Should().NotBeNull();
        outerProgress!.HasStarted.Should().BeTrue();
        outerProgress.IsCompleted.Should().BeTrue();
        outerProgress.Iterations.Select(item => item.Iteration.Number)
            .Should().Equal(1, 2, 3);
        outerProgress.Iterations[0].CommitStep.Should().NotBeNull();
        outerProgress.Iterations[0].Outcome.Should().Be(LoopBodyOutcomeKind.Continue);
        outerProgress.Iterations[0].IsCommitted.Should().BeTrue();
        outerProgress.Iterations[0].Error.Should().BeNull();
        outerProgress.Iterations[1].CommitStep.Should().NotBeNull();
        outerProgress.Iterations[2].CommitStep.Should().BeNull();
        outerProgress.Iterations[2].WasEntered.Should().BeFalse();
        outerProgress.CurrentIteration!.Iteration.DisplayPath.Should().Be("outer[3]");
        outerProgress.FinalStep!.Status.Should().Be(StepStatus.Completed);

        var inner = outer.Iteration(2).NestedLoop("inner");
        var handle = engine.GetHandle<string>(workflow.RunId);
        var innerProgress = await handle.GetLoopProgressAsync(
            inner,
            TestContext.Current.CancellationToken);

        innerProgress.Should().NotBeNull();
        innerProgress!.IsCompleted.Should().BeTrue();
        innerProgress.Iterations.Select(item => item.Iteration.Number)
            .Should().Equal(1, 2, 3);
        innerProgress.Iterations[0].BodySteps.Should().ContainSingle();
        innerProgress.Iterations[0].BodySteps[0].Status
            .Should().Be(StepStatus.Completed);
        innerProgress.FinalStep!.Status.Should().Be(StepStatus.Completed);
    }

    [Fact]
    public async Task TypedRestartPreviewAndReceipt_TargetNestedBodyIdempotently()
    {
        var workflow = new InspectableNestedLoopWorkflow();
        var engine = CreateEngine(workflow, "loop-typed-restart");
        await engine.RunAsync<string, string>(
            "loop-typed-restart",
            "1",
            "ignored",
            cancellationToken: TestContext.Current.CancellationToken);
        var target = WorkflowLoopReference.Root("outer")
            .Iteration(2)
            .NestedLoop("inner")
            .Iteration(1)
            .BodyStep("advance");
        var handle = engine.GetHandle<string>(workflow.RunId);

        var preview = await handle.PlanLoopRestartAsync(
            target,
            cancellationToken: TestContext.Current.CancellationToken);
        preview.TargetStepKey.Should().Be(
            "$loop/outer/2/loop/inner/1/body/advance");
        preview.StepsToInvalidate.Should().Contain(item =>
            item.StepKey == "$loop/outer/2/commit");
        (await engine.GetRunAsync(
            workflow.RunId,
            TestContext.Current.CancellationToken))!.Status
            .Should().Be(WorkflowStatus.Completed);

        var options = new RestartStepOptions
        {
            OperationId = Guid.NewGuid(),
            Actor = "operator",
            Reason = "re-evaluate inner result"
        };
        var applied = await handle.RestartLoopStepWithReceiptAsync(
            target,
            options,
            TestContext.Current.CancellationToken);
        var repeated = await handle.RestartLoopStepWithReceiptAsync(
            target,
            options,
            TestContext.Current.CancellationToken);

        applied.WasApplied.Should().BeTrue();
        repeated.WasApplied.Should().BeFalse();
        repeated.Event.Sequence.Should().Be(applied.Event.Sequence);
        var progress = await handle.GetLoopProgressAsync(
            target.Loop,
            TestContext.Current.CancellationToken);
        progress!.Iterations[0].BodySteps.Single().Revision.Should().Be(2);
        progress.Iterations[0].BodySteps.Single().Status.Should().Be(StepStatus.Pending);
    }

    [Fact]
    public async Task GetLoopProgressAsync_ReturnsNullOnlyForMissingRun()
    {
        var store = CreateStore();
        var engine = new WorkflowEngine(store, new WorkflowRegistry());
        var missing = await engine.GetLoopProgressAsync(
            Guid.NewGuid(),
            WorkflowLoopReference.Root("loop"),
            TestContext.Current.CancellationToken);

        missing.Should().BeNull();
    }

    private sealed class InspectableNestedLoopWorkflow : IWorkflow<string, string>
    {
        public Guid RunId { get; private set; }

        public async Task<string> RunAsync(
            WorkflowContext context,
            string input,
            CancellationToken cancellationToken)
        {
            RunId = context.WorkflowRunId;
            var result = await context.LoopAsync(
                "outer",
                0,
                state => state < 2,
                async (outer, token) =>
                {
                    await outer.LoopAsync(
                        "inner",
                        0,
                        state => state < 2,
                        async (inner, innerToken) =>
                        {
                            var next = await inner.StepAsync(
                                "advance",
                                (_, _) => Task.FromResult(inner.State + 1),
                                cancellationToken: innerToken);
                            return inner.Continue(next);
                        },
                        new LoopOptions(maxIterations: 3),
                        token);
                    return outer.Continue(outer.State + 1);
                },
                new LoopOptions(maxIterations: 3),
                cancellationToken);
            return result.ToString();
        }
    }
}
