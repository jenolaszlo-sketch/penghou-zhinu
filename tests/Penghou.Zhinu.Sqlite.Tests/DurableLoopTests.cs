using FluentAssertions;

namespace Penghou.Zhinu.Sqlite.Tests;

public sealed class DurableLoopTests : WorkflowEngineTestBase
{
    [Fact]
    public async Task LoopAsync_CommitsTypedStateAndEmitsDurableBoundaries()
    {
        var workflow = new CountingLoopWorkflow();
        var engine = CreateEngine(workflow, "durable-loop");

        var result = await engine.RunAsync<string, string>(
            "durable-loop",
            "1",
            "3",
            cancellationToken: TestContext.Current.CancellationToken);

        result.Should().Be("3");
        workflow.BodyCalls.Should().Be(3);
        var steps = await engine.GetStepsAsync(
            workflow.RunId,
            TestContext.Current.CancellationToken);
        steps.Should().HaveCount(11)
            .And.OnlyContain(step => step.Status == StepStatus.Completed);
        steps.Should().Contain(step => step.StepKey == "$loop/refinement/2/body/advance");
        steps.Should().Contain(step => step.StepKey == "$loop/refinement/3/commit");
        steps.Should().Contain(step => step.StepKey == "refinement");

        var dependencies = await engine.GetDependencyGraphAsync(
            workflow.RunId,
            TestContext.Current.CancellationToken);
        dependencies.Should().Contain(new StepDependency(
            "$loop/refinement/2/commit",
            "$loop/refinement/2/body/advance"));
        dependencies.Should().Contain(new StepDependency(
            "$loop/refinement/3/condition",
            "$loop/refinement/2/commit"));
        dependencies.Should().Contain(new StepDependency(
            "refinement",
            "$loop/refinement/4/condition"));

        var events = await engine.GetEventsAsync(
            workflow.RunId,
            cancellationToken: TestContext.Current.CancellationToken);
        events.Count(item =>
            item.EventType == WorkflowEventTypes.LoopIterationCommitted)
            .Should().Be(3);
        events.Count(item => item.EventType == WorkflowEventTypes.LoopCompleted)
            .Should().Be(1);
    }

    [Fact]
    public async Task LoopAsync_RestartingOneBodyInvalidatesThatAndLaterIterations()
    {
        var first = new CountingLoopWorkflow();
        var engine = CreateEngine(first, "durable-loop-restart");
        await engine.RunAsync<string, string>(
            "durable-loop-restart",
            "1",
            "3",
            cancellationToken: TestContext.Current.CancellationToken);

        var plan = await engine.RestartStepAsync(
            first.RunId,
            "$loop/refinement/2/body/advance",
            TestContext.Current.CancellationToken);

        plan.StepsToInvalidate.Should().Contain(step =>
            step.StepKey == "$loop/refinement/2/commit");
        plan.StepsToInvalidate.Should().Contain(step =>
            step.StepKey == "$loop/refinement/3/body/advance");
        plan.StepsToInvalidate.Should().Contain(step =>
            step.StepKey == "refinement");
        plan.StepsToInvalidate.Should().NotContain(step =>
            step.StepKey == "$loop/refinement/1/body/advance");

        var replay = new CountingLoopWorkflow();
        var reopened = CreateEngine(replay, "durable-loop-restart");
        await reopened.ExecuteAsync(
            first.RunId,
            TestContext.Current.CancellationToken);
        var result = await reopened.WaitForCompletionAsync<string>(
            first.RunId,
            cancellationToken: TestContext.Current.CancellationToken);

        result.Should().Be("3");
        replay.BodyCalls.Should().Be(2);
        var steps = await reopened.GetStepsAsync(
            first.RunId,
            TestContext.Current.CancellationToken);
        steps.Single(step => step.StepKey == "$loop/refinement/1/body/advance")
            .Revision.Should().Be(1);
        steps.Single(step => step.StepKey == "$loop/refinement/2/body/advance")
            .Revision.Should().Be(2);
        steps.Single(step => step.StepKey == "$loop/refinement/3/body/advance")
            .Revision.Should().Be(2);
    }

    [Fact]
    public async Task LoopAsync_TrueConditionAfterMaximumFailsWithTypedEvidence()
    {
        var workflow = new UnboundedLoopWorkflow();
        var engine = CreateEngine(workflow, "durable-loop-limit");

        var action = () => engine.RunAsync<string, string>(
            "durable-loop-limit",
            "1",
            "ignored",
            cancellationToken: TestContext.Current.CancellationToken);

        var failure = await action.Should()
            .ThrowAsync<WorkflowExecutionFailedException>();
        failure.Which.Error.Type.Should().Be(
            typeof(LoopLimitExceededException).FullName);
        failure.Which.Error.Message.Should().Contain("limit of 2 iterations");
        workflow.BodyCalls.Should().Be(2);

        var events = await engine.GetEventsAsync(
            workflow.RunId,
            cancellationToken: TestContext.Current.CancellationToken);
        events.Count(item =>
            item.EventType == WorkflowEventTypes.LoopLimitExceeded)
            .Should().Be(1);
        events.Should().Contain(item =>
            item.EventType == WorkflowEventTypes.WorkflowFailed);
    }

    [Fact]
    public async Task LoopAsync_BreakCommitsFinalStateWithoutAnotherCondition()
    {
        var workflow = new BreakingLoopWorkflow();
        var engine = CreateEngine(workflow, "durable-loop-break");

        var result = await engine.RunAsync<string, string>(
            "durable-loop-break",
            "1",
            "ignored",
            cancellationToken: TestContext.Current.CancellationToken);

        result.Should().Be("2");
        workflow.BodyCalls.Should().Be(2);
        workflow.ConditionCalls.Should().Be(2);
        var steps = await engine.GetStepsAsync(
            workflow.RunId,
            TestContext.Current.CancellationToken);
        steps.Should().HaveCount(7);
        steps.Should().Contain(step =>
            step.StepKey == "$loop/refinement/2/commit" &&
            step.OutputJson!.Contains(
                "\"kind\":\"Break\"",
                StringComparison.Ordinal));
        steps.Should().NotContain(step =>
            step.StepKey == "$loop/refinement/3/condition");

        var events = await engine.GetEventsAsync(
            workflow.RunId,
            cancellationToken: TestContext.Current.CancellationToken);
        events.Count(item => item.EventType == WorkflowEventTypes.LoopCompleted)
            .Should().Be(1);
        events.Single(item => item.EventType == WorkflowEventTypes.LoopCompleted)
            .DataJson.Should().Contain("\"reason\":\"Break\"");
    }

    [Fact]
    public async Task LoopAsync_ReusesCommittedBreakWithoutReenteringBody()
    {
        var first = new BreakingLoopWorkflow();
        var engine = CreateEngine(first, "durable-loop-break-replay");
        await engine.RunAsync<string, string>(
            "durable-loop-break-replay",
            "1",
            "ignored",
            cancellationToken: TestContext.Current.CancellationToken);
        await engine.RestartStepAsync(
            first.RunId,
            "refinement",
            TestContext.Current.CancellationToken);

        var replay = new BreakingLoopWorkflow();
        var reopened = CreateEngine(replay, "durable-loop-break-replay");
        await reopened.ExecuteAsync(
            first.RunId,
            TestContext.Current.CancellationToken);
        var result = await reopened.WaitForCompletionAsync<string>(
            first.RunId,
            cancellationToken: TestContext.Current.CancellationToken);

        result.Should().Be("2");
        replay.BodyCalls.Should().Be(0);
        replay.ConditionCalls.Should().Be(0);
        var steps = await reopened.GetStepsAsync(
            first.RunId,
            TestContext.Current.CancellationToken);
        steps.Single(step => step.StepKey == "refinement")
            .Revision.Should().Be(2);
        steps.Single(step => step.StepKey == "$loop/refinement/2/commit")
            .Revision.Should().Be(1);
    }

    [Fact]
    public async Task LoopAsync_RestartingBreakBodyRecomputesItsDisposition()
    {
        var first = new BreakingLoopWorkflow();
        var engine = CreateEngine(first, "durable-loop-break-restart");
        await engine.RunAsync<string, string>(
            "durable-loop-break-restart",
            "1",
            "ignored",
            cancellationToken: TestContext.Current.CancellationToken);

        var plan = await engine.RestartStepAsync(
            first.RunId,
            "$loop/refinement/2/body/advance",
            TestContext.Current.CancellationToken);
        plan.StepsToInvalidate.Should().Contain(step =>
            step.StepKey == "$loop/refinement/2/commit");
        plan.StepsToInvalidate.Should().Contain(step =>
            step.StepKey == "refinement");

        var replay = new BreakingLoopWorkflow();
        var reopened = CreateEngine(replay, "durable-loop-break-restart");
        await reopened.ExecuteAsync(
            first.RunId,
            TestContext.Current.CancellationToken);
        var result = await reopened.WaitForCompletionAsync<string>(
            first.RunId,
            cancellationToken: TestContext.Current.CancellationToken);

        result.Should().Be("2");
        replay.BodyCalls.Should().Be(1);
        replay.ConditionCalls.Should().Be(0);
        var steps = await reopened.GetStepsAsync(
            first.RunId,
            TestContext.Current.CancellationToken);
        steps.Single(step => step.StepKey == "$loop/refinement/1/commit")
            .Revision.Should().Be(1);
        steps.Single(step => step.StepKey == "$loop/refinement/2/commit")
            .Revision.Should().Be(2);
        steps.Single(step => step.StepKey == "refinement")
            .Revision.Should().Be(2);
    }

    [Fact]
    public async Task LoopAsync_RejectsOutcomeCreatedByAnotherIterationScope()
    {
        var workflow = new ForeignOutcomeLoopWorkflow();
        var engine = CreateEngine(workflow, "durable-loop-foreign-outcome");

        var action = () => engine.RunAsync<string, string>(
            "durable-loop-foreign-outcome",
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
    public async Task LoopAsync_RollbackReplayRebindsBodyCompensations()
    {
        var forward = new CompensatingLoopWorkflow();
        var engine = CreateEngine(forward, "durable-loop-compensation");
        await engine.RunAsync<string, string>(
            "durable-loop-compensation",
            "1",
            "ignored",
            cancellationToken: TestContext.Current.CancellationToken);

        var rollback = new CompensatingLoopWorkflow();
        var rollbackEngine = CreateEngine(
            rollback,
            "durable-loop-compensation");
        await rollbackEngine.RollbackAsync(
            forward.RunId,
            cancellationToken: TestContext.Current.CancellationToken);

        rollback.BodyEntries.Should().Be(2);
        rollback.CompensatedStates.Should().Equal(2, 1);
        (await rollbackEngine.GetRunAsync(
            forward.RunId,
            TestContext.Current.CancellationToken))!.Status
            .Should().Be(WorkflowStatus.Compensated);
    }

    private sealed class CountingLoopWorkflow : IWorkflow<string, string>
    {
        public int BodyCalls;

        public Guid RunId { get; private set; }

        public async Task<string> RunAsync(
            WorkflowContext context,
            string input,
            CancellationToken cancellationToken)
        {
            RunId = context.WorkflowRunId;
            var target = int.Parse(input, System.Globalization.CultureInfo.InvariantCulture);
            var result = await context.LoopAsync(
                "refinement",
                0,
                state => state < target,
                async (iteration, token) =>
                {
                    var nextState = await iteration.StepAsync(
                        "advance",
                        iteration.State,
                        (state, _, _) =>
                        {
                            Interlocked.Increment(ref BodyCalls);
                            return Task.FromResult(state + 1);
                        },
                        cancellationToken: token);
                    return iteration.Continue(nextState);
                },
                new LoopOptions(maxIterations: 10),
                cancellationToken);
            return result.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    private sealed class UnboundedLoopWorkflow : IWorkflow<string, string>
    {
        public int BodyCalls;

        public Guid RunId { get; private set; }

        public async Task<string> RunAsync(
            WorkflowContext context,
            string input,
            CancellationToken cancellationToken)
        {
            RunId = context.WorkflowRunId;
            var result = await context.LoopAsync(
                "unbounded",
                0,
                _ => true,
                async (iteration, token) =>
                {
                    var nextState = await iteration.StepAsync(
                        "advance",
                        iteration.State,
                        (state, _, _) =>
                        {
                            Interlocked.Increment(ref BodyCalls);
                            return Task.FromResult(state + 1);
                        },
                        cancellationToken: token);
                    return iteration.Continue(nextState);
                },
                new LoopOptions(maxIterations: 2),
                cancellationToken);
            return $"{input}:{result}";
        }
    }

    private sealed class BreakingLoopWorkflow : IWorkflow<string, string>
    {
        public int BodyCalls;
        public int ConditionCalls;

        public Guid RunId { get; private set; }

        public async Task<string> RunAsync(
            WorkflowContext context,
            string input,
            CancellationToken cancellationToken)
        {
            RunId = context.WorkflowRunId;
            var result = await context.LoopAsync(
                "refinement",
                0,
                _ =>
                {
                    Interlocked.Increment(ref ConditionCalls);
                    return true;
                },
                async (iteration, token) =>
                {
                    var nextState = await iteration.StepAsync(
                        "advance",
                        iteration.State,
                        (state, _, _) =>
                        {
                            Interlocked.Increment(ref BodyCalls);
                            return Task.FromResult(state + 1);
                        },
                        cancellationToken: token);
                    return nextState == 2
                        ? iteration.Break(nextState)
                        : iteration.Continue(nextState);
                },
                new LoopOptions(maxIterations: 10),
                cancellationToken);
            return result.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    private sealed class ForeignOutcomeLoopWorkflow : IWorkflow<string, string>
    {
        private LoopBodyOutcome<int>? firstOutcome;

        public async Task<string> RunAsync(
            WorkflowContext context,
            string input,
            CancellationToken cancellationToken)
        {
            var result = await context.LoopAsync(
                "scoped",
                0,
                state => state < 2,
                (iteration, _) =>
                {
                    if (firstOutcome is null)
                    {
                        firstOutcome = iteration.Continue(1);
                        return Task.FromResult(firstOutcome);
                    }
                    return Task.FromResult(firstOutcome);
                },
                new LoopOptions(maxIterations: 3),
                cancellationToken);
            return $"{input}:{result}";
        }
    }

    private sealed class CompensatingLoopWorkflow : IWorkflow<string, string>
    {
        public int BodyEntries;

        public List<int> CompensatedStates { get; } = [];

        public Guid RunId { get; private set; }

        public async Task<string> RunAsync(
            WorkflowContext context,
            string input,
            CancellationToken cancellationToken)
        {
            RunId = context.WorkflowRunId;
            var result = await context.LoopAsync(
                "compensating",
                0,
                state => state < 2,
                async (iteration, token) =>
                {
                    Interlocked.Increment(ref BodyEntries);
                    var nextState = await iteration.StepAsync(
                        "advance",
                        iteration.State,
                        (state, _, _) => Task.FromResult(state + 1),
                        cancellationToken: token,
                        compensation: (state, _, _) =>
                        {
                            CompensatedStates.Add(state);
                            return Task.CompletedTask;
                        });
                    return iteration.Continue(nextState);
                },
                new LoopOptions(maxIterations: 3),
                cancellationToken);
            return $"{input}:{result}";
        }
    }
}
