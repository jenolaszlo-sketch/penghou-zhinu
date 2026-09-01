using FluentAssertions;

namespace Penghou.Zhinu.Sqlite.Tests;

public sealed class DurableLoopFailureTests : WorkflowEngineTestBase
{
    [Fact]
    public async Task ConditionFailure_CanRestartWithoutRunningBodyPrematurely()
    {
        var failing = new RecoverableConditionWorkflow(failCondition: true);
        var engine = CreateEngine(failing, "loop-condition-failure");
        var action = () => engine.RunAsync<string, string>(
            "loop-condition-failure",
            "1",
            "ignored",
            cancellationToken: TestContext.Current.CancellationToken);

        await action.Should().ThrowAsync<WorkflowExecutionFailedException>();
        failing.BodyCalls.Should().Be(0);
        var condition = (await engine.GetStepsAsync(
                failing.RunId,
                TestContext.Current.CancellationToken))
            .Single(step => step.StepKey == "$loop/refinement/1/condition");
        condition.Status.Should().Be(StepStatus.Failed);

        await engine.RestartStepAsync(
            failing.RunId,
            condition.StepKey,
            TestContext.Current.CancellationToken);
        var resumed = new RecoverableConditionWorkflow(failCondition: false);
        var recovery = CreateEngine(resumed, "loop-condition-failure");
        await recovery.ExecuteAsync(
            failing.RunId,
            TestContext.Current.CancellationToken);
        var result = await recovery.WaitForCompletionAsync<string>(
            failing.RunId,
            cancellationToken: TestContext.Current.CancellationToken);

        result.Should().Be("1");
        resumed.BodyCalls.Should().Be(1);
    }

    [Fact]
    public async Task BodyStepRetry_CommitsOneIterationAfterTransientFailure()
    {
        var workflow = new RetryingBodyWorkflow(alwaysFail: false);
        var engine = CreateEngine(workflow, "loop-body-retry");

        var result = await engine.RunAsync<string, string>(
            "loop-body-retry",
            "1",
            "ignored",
            cancellationToken: TestContext.Current.CancellationToken);

        result.Should().Be("1");
        workflow.OperationCalls.Should().Be(2);
        var body = (await engine.GetStepsAsync(
                workflow.RunId,
                TestContext.Current.CancellationToken))
            .Single(step => step.StepKey == "$loop/refinement/1/body/advance");
        body.Attempt.Should().Be(2);
        body.Status.Should().Be(StepStatus.Completed);
        var events = await engine.GetEventsAsync(
            workflow.RunId,
            cancellationToken: TestContext.Current.CancellationToken);
        events.Count(item =>
            item.EventType == WorkflowEventTypes.LoopIterationCommitted)
            .Should().Be(1);
    }

    [Fact]
    public async Task BodyStepFailure_CanRestartAndReuseCondition()
    {
        var failing = new RetryingBodyWorkflow(alwaysFail: true);
        var engine = CreateEngine(failing, "loop-body-failure");
        var action = () => engine.RunAsync<string, string>(
            "loop-body-failure",
            "1",
            "ignored",
            cancellationToken: TestContext.Current.CancellationToken);

        await action.Should().ThrowAsync<WorkflowExecutionFailedException>();
        var bodyKey = "$loop/refinement/1/body/advance";
        await engine.RestartStepAsync(
            failing.RunId,
            bodyKey,
            TestContext.Current.CancellationToken);

        var resumed = new RetryingBodyWorkflow(alwaysFail: false);
        var recovery = CreateEngine(resumed, "loop-body-failure");
        await recovery.ExecuteAsync(
            failing.RunId,
            TestContext.Current.CancellationToken);
        var result = await recovery.WaitForCompletionAsync<string>(
            failing.RunId,
            cancellationToken: TestContext.Current.CancellationToken);

        result.Should().Be("1");
        resumed.ConditionCalls.Should().Be(1);
        resumed.OperationCalls.Should().Be(2);
        var steps = await recovery.GetStepsAsync(
            failing.RunId,
            TestContext.Current.CancellationToken);
        steps.Single(step => step.StepKey == "$loop/refinement/1/condition")
            .Revision.Should().Be(1);
        steps.Single(step => step.StepKey == bodyKey)
            .Revision.Should().Be(2);
    }

    private sealed class RecoverableConditionWorkflow(bool failCondition) :
        IWorkflow<string, string>
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
                "refinement",
                0,
                state =>
                {
                    if (failCondition)
                        throw new InvalidOperationException("condition failed");
                    return state < 1;
                },
                (iteration, _) =>
                {
                    Interlocked.Increment(ref BodyCalls);
                    return Task.FromResult(iteration.Continue(1));
                },
                new LoopOptions(maxIterations: 2),
                cancellationToken);
            return result.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    private sealed class RetryingBodyWorkflow(bool alwaysFail) :
        IWorkflow<string, string>
    {
        public int ConditionCalls;
        public int OperationCalls;

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
                state =>
                {
                    Interlocked.Increment(ref ConditionCalls);
                    return state < 1;
                },
                async (iteration, token) =>
                {
                    var next = await iteration.StepAsync(
                        "advance",
                        iteration.State,
                        (state, _, _) =>
                        {
                            var call = Interlocked.Increment(ref OperationCalls);
                            if (alwaysFail || call == 1)
                                throw new InvalidOperationException("body failed");
                            return Task.FromResult(state + 1);
                        },
                        new StepOptions
                        {
                            Retry = new RetryPolicy { MaxAttempts = 2 }
                        },
                        token);
                    return iteration.Continue(next);
                },
                new LoopOptions(maxIterations: 2),
                cancellationToken);
            return result.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
