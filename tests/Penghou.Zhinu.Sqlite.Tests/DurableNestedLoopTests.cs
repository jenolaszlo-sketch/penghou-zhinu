using FluentAssertions;

namespace Penghou.Zhinu.Sqlite.Tests;

public sealed class DurableNestedLoopTests : WorkflowEngineTestBase
{
    [Fact]
    public async Task NestedLoops_SameInnerNameAcrossOuterIterationsDoNotCollide()
    {
        var workflow = new NestedLoopWorkflow();
        var engine = CreateEngine(workflow, "nested-loop");

        var result = await engine.RunAsync<string, string>(
            "nested-loop",
            "1",
            "ignored",
            cancellationToken: TestContext.Current.CancellationToken);

        result.Should().Be("2");
        workflow.OuterBodyEntries.Should().Be(2);
        workflow.InnerOperationCalls.Should().Be(4);
        var steps = await engine.GetStepsAsync(
            workflow.RunId,
            TestContext.Current.CancellationToken);
        steps.Should().Contain(step =>
            step.StepKey == "$loop/outer/1/loop/inner/1/body/advance");
        steps.Should().Contain(step =>
            step.StepKey == "$loop/outer/2/loop/inner/1/body/advance");
        steps.Should().Contain(step =>
            step.StepKey == "$loop/outer/1/loop/inner");
        steps.Should().Contain(step =>
            step.StepKey == "$loop/outer/2/loop/inner");
        steps.Select(step => step.StepKey).Should().OnlyHaveUniqueItems();

        var dependencies = await engine.GetDependencyGraphAsync(
            workflow.RunId,
            TestContext.Current.CancellationToken);
        dependencies.Should().Contain(new StepDependency(
            "$loop/outer/1/loop/inner/1/condition",
            "$loop/outer/1/condition"));
        dependencies.Should().Contain(new StepDependency(
            "$loop/outer/1/commit",
            "$loop/outer/1/loop/inner"));
        dependencies.Should().Contain(new StepDependency(
            "$loop/outer/2/commit",
            "$loop/outer/2/loop/inner"));
    }

    [Fact]
    public async Task NestedLoops_RestartingInnerBodyInvalidatesContainingOuterWork()
    {
        var first = new NestedLoopWorkflow();
        var engine = CreateEngine(first, "nested-loop-inner-restart");
        await engine.RunAsync<string, string>(
            "nested-loop-inner-restart",
            "1",
            "ignored",
            cancellationToken: TestContext.Current.CancellationToken);

        const string innerBody =
            "$loop/outer/2/loop/inner/1/body/advance";
        var plan = await engine.RestartStepAsync(
            first.RunId,
            innerBody,
            TestContext.Current.CancellationToken);

        plan.StepsToInvalidate.Should().Contain(step =>
            step.StepKey == "$loop/outer/2/loop/inner");
        plan.StepsToInvalidate.Should().Contain(step =>
            step.StepKey == "$loop/outer/2/commit");
        plan.StepsToInvalidate.Should().Contain(step => step.StepKey == "outer");
        plan.StepsToInvalidate.Should().NotContain(step =>
            step.StepKey == "$loop/outer/1/commit");

        var replay = new NestedLoopWorkflow();
        var recovery = CreateEngine(replay, "nested-loop-inner-restart");
        await recovery.ExecuteAsync(
            first.RunId,
            TestContext.Current.CancellationToken);
        var result = await recovery.WaitForCompletionAsync<string>(
            first.RunId,
            cancellationToken: TestContext.Current.CancellationToken);

        result.Should().Be("2");
        replay.OuterBodyEntries.Should().Be(1);
        replay.InnerOperationCalls.Should().Be(2);
        var steps = await recovery.GetStepsAsync(
            first.RunId,
            TestContext.Current.CancellationToken);
        steps.Single(step => step.StepKey == "$loop/outer/1/commit")
            .Revision.Should().Be(1);
        steps.Single(step => step.StepKey == innerBody)
            .Revision.Should().Be(2);
        steps.Single(step => step.StepKey == "$loop/outer/2/commit")
            .Revision.Should().Be(2);
    }

    [Fact]
    public async Task NestedLoops_RestartingOuterCommitReusesCompletedInnerResult()
    {
        var first = new NestedLoopWorkflow();
        var engine = CreateEngine(first, "nested-loop-outer-restart");
        await engine.RunAsync<string, string>(
            "nested-loop-outer-restart",
            "1",
            "ignored",
            cancellationToken: TestContext.Current.CancellationToken);
        await engine.RestartStepAsync(
            first.RunId,
            "$loop/outer/2/commit",
            TestContext.Current.CancellationToken);

        var replay = new NestedLoopWorkflow();
        var recovery = CreateEngine(replay, "nested-loop-outer-restart");
        await recovery.ExecuteAsync(
            first.RunId,
            TestContext.Current.CancellationToken);
        var result = await recovery.WaitForCompletionAsync<string>(
            first.RunId,
            cancellationToken: TestContext.Current.CancellationToken);

        result.Should().Be("2");
        replay.OuterBodyEntries.Should().Be(1);
        replay.InnerOperationCalls.Should().Be(0);
        var steps = await recovery.GetStepsAsync(
            first.RunId,
            TestContext.Current.CancellationToken);
        steps.Single(step =>
                step.StepKey == "$loop/outer/2/loop/inner/1/body/advance")
            .Revision.Should().Be(1);
        steps.Single(step => step.StepKey == "$loop/outer/2/commit")
            .Revision.Should().Be(2);
    }

    [Fact]
    public async Task NestedLoops_RejectOuterOutcomeReturnedFromInnerScope()
    {
        var workflow = new NonLocalBreakWorkflow();
        var engine = CreateEngine(workflow, "nested-loop-non-local-break");
        var action = () => engine.RunAsync<string, string>(
            "nested-loop-non-local-break",
            "1",
            "ignored",
            cancellationToken: TestContext.Current.CancellationToken);

        var failure = await action.Should()
            .ThrowAsync<WorkflowExecutionFailedException>();
        failure.Which.Error.Type.Should().Be(
            typeof(WorkflowStateException).FullName);
        failure.Which.Error.Message.Should().Contain("different loop scope");
    }

    [Fact]
    public async Task NestedLoops_RespectConfiguredLexicalDepth()
    {
        var workflow = new NestedLoopWorkflow();
        var engine = CreateEngine(
            workflow,
            "nested-loop-depth",
            new ZhinuOptions { MaxLoopNestingDepth = 1 });
        var action = () => engine.RunAsync<string, string>(
            "nested-loop-depth",
            "1",
            "ignored",
            cancellationToken: TestContext.Current.CancellationToken);

        var failure = await action.Should()
            .ThrowAsync<WorkflowExecutionFailedException>();
        failure.Which.Error.Type.Should().Be(
            typeof(LoopNestingLimitExceededException).FullName);
        failure.Which.Error.Message.Should().Contain("configured maximum of 1");
    }

    [Fact]
    public void NestedIdentity_DefaultMaximumNamesRemainWithinEncodedKeyLimit()
    {
        var name = new string('a', 128);
        var scope = DurableLoopScope.Root(name);
        for (var depth = 2; depth <= 16; depth++)
            scope = scope.Nest(scope.Iteration(1), name);

        DurableLoopStepKeys.Commit(scope.Iteration(1)).Length
            .Should().BeLessThan(4096);
    }

    private WorkflowEngine CreateEngine(
        IWorkflow<string, string> workflow,
        string name,
        ZhinuOptions options) =>
        new(
            CreateStore(),
            new WorkflowRegistry().Register(name, "1", workflow),
            options);

    private sealed class NestedLoopWorkflow : IWorkflow<string, string>
    {
        public int InnerOperationCalls;
        public int OuterBodyEntries;

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
                    Interlocked.Increment(ref OuterBodyEntries);
                    var innerResult = await outer.LoopAsync(
                        "inner",
                        0,
                        _ => true,
                        async (inner, innerToken) =>
                        {
                            var next = await inner.StepAsync(
                                "advance",
                                inner.State,
                                (state, _, _) =>
                                {
                                    Interlocked.Increment(ref InnerOperationCalls);
                                    return Task.FromResult(state + 1);
                                },
                                cancellationToken: innerToken);
                            return next == 2
                                ? inner.Break(next)
                                : inner.Continue(next);
                        },
                        new LoopOptions(maxIterations: 3),
                        token);
                    innerResult.Should().Be(2);
                    return outer.Continue(outer.State + 1);
                },
                new LoopOptions(maxIterations: 3),
                cancellationToken);
            return result.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    private sealed class NonLocalBreakWorkflow : IWorkflow<string, string>
    {
        public async Task<string> RunAsync(
            WorkflowContext context,
            string input,
            CancellationToken cancellationToken)
        {
            var result = await context.LoopAsync(
                "outer",
                0,
                _ => true,
                async (outer, token) =>
                {
                    await outer.LoopAsync(
                        "inner",
                        0,
                        _ => true,
                        (_, _) => Task.FromResult(outer.Break(1)),
                        new LoopOptions(maxIterations: 1),
                        token);
                    return outer.Continue(1);
                },
                new LoopOptions(maxIterations: 1),
                cancellationToken);
            return $"{input}:{result}";
        }
    }
}
