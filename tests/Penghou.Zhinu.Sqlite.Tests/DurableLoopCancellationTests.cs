using FluentAssertions;

namespace Penghou.Zhinu.Sqlite.Tests;

public sealed class DurableLoopCancellationTests : WorkflowEngineTestBase
{
    [Fact]
    public async Task ExecutionTokenCancellation_InterruptsAndRemainsResumable()
    {
        var interrupted = new ControlledLoopWorkflow(
            blockSecondIteration: true,
            observeCancellation: true);
        var engine = CreateLoopEngine(interrupted, "loop-host-interruption");
        var runId = await engine.StartAsync(
            "loop-host-interruption",
            "1",
            "ignored",
            cancellationToken: TestContext.Current.CancellationToken);
        using var executionCancellation = new CancellationTokenSource();
        var execution = engine.ExecuteAsync(runId, executionCancellation.Token);
        await interrupted.SecondIterationStarted.Task.WaitAsync(
            TestContext.Current.CancellationToken);

        await executionCancellation.CancelAsync();
        await execution;

        var interruptedRun = await engine.GetRunAsync(
            runId,
            TestContext.Current.CancellationToken);
        interruptedRun!.Status.Should().Be(WorkflowStatus.Running);
        interruptedRun.LeaseOwner.Should().BeNull();
        var interruptedProgress = await engine.GetLoopProgressAsync(
            runId,
            WorkflowLoopReference.Root("refinement"),
            TestContext.Current.CancellationToken);
        interruptedProgress!.Iterations[0].IsCommitted.Should().BeTrue();
        interruptedProgress.Iterations[1].CommitStep.Should().BeNull();
        var interruptedEvents = await engine.GetEventsAsync(
            runId,
            cancellationToken: TestContext.Current.CancellationToken);
        interruptedEvents.Should().NotContain(item =>
            item.EventType == WorkflowEventTypes.WorkflowCancelled);

        var resumed = new ControlledLoopWorkflow(
            blockSecondIteration: false,
            observeCancellation: true);
        var recovery = CreateLoopEngine(resumed, "loop-host-interruption");
        await recovery.ExecuteAsync(runId, TestContext.Current.CancellationToken);
        var result = await recovery.WaitForCompletionAsync<string>(
            runId,
            cancellationToken: TestContext.Current.CancellationToken);

        result.Should().Be("2");
        resumed.BodyCalls.Should().Be(1);
        var recoveredEvents = await recovery.GetEventsAsync(
            runId,
            cancellationToken: TestContext.Current.CancellationToken);
        recoveredEvents.Should().Contain(item =>
            item.EventType == WorkflowEventTypes.WorkflowResumed);
        recoveredEvents.Should().Contain(item =>
            item.EventType == WorkflowEventTypes.WorkflowCompleted);
        recoveredEvents.Should().NotContain(item =>
            item.EventType == WorkflowEventTypes.WorkflowCancelled);
    }

    [Fact]
    public async Task CancelAsync_IsTerminalAndFencesCancellationResistantBody()
    {
        var cancelled = new ControlledLoopWorkflow(
            blockSecondIteration: true,
            observeCancellation: false);
        var engine = CreateLoopEngine(cancelled, "loop-durable-cancel");
        var runId = await engine.StartAsync(
            "loop-durable-cancel",
            "1",
            "ignored",
            cancellationToken: TestContext.Current.CancellationToken);
        var execution = engine.ExecuteAsync(
            runId,
            TestContext.Current.CancellationToken);
        await cancelled.SecondIterationStarted.Task.WaitAsync(
            TestContext.Current.CancellationToken);

        await engine.CancelAsync(
            runId,
            actor: "user",
            reason: "stop requested",
            cancellationToken: TestContext.Current.CancellationToken);
        cancelled.ReleaseSecondIteration();
        await execution;

        var run = await engine.GetRunAsync(
            runId,
            TestContext.Current.CancellationToken);
        run!.Status.Should().Be(WorkflowStatus.Cancelled);
        run.LeaseOwner.Should().BeNull();
        var progress = await engine.GetLoopProgressAsync(
            runId,
            WorkflowLoopReference.Root("refinement"),
            TestContext.Current.CancellationToken);
        progress!.Iterations[0].IsCommitted.Should().BeTrue();
        progress.Iterations[1].BodySteps.Single().Status
            .Should().Be(StepStatus.Cancelled);
        progress.Iterations[1].CommitStep.Should().BeNull();
        progress.FinalStep.Should().BeNull();

        await engine.CancelAsync(
            runId,
            actor: "user",
            reason: "duplicate request",
            cancellationToken: TestContext.Current.CancellationToken);
        var events = await engine.GetEventsAsync(
            runId,
            cancellationToken: TestContext.Current.CancellationToken);
        events.Count(item =>
            item.EventType == WorkflowEventTypes.WorkflowCancelled).Should().Be(1);
        events.Should().NotContain(item =>
            item.EventType == WorkflowEventTypes.LoopIterationCommitted &&
            item.StepKey == "$loop/refinement/2/commit");

        var attemptedReplay = new ControlledLoopWorkflow(
            blockSecondIteration: false,
            observeCancellation: true);
        var reopened = CreateLoopEngine(attemptedReplay, "loop-durable-cancel");
        await reopened.ExecuteAsync(runId, TestContext.Current.CancellationToken);
        attemptedReplay.BodyCalls.Should().Be(0);
        var wait = () => reopened.WaitForCompletionAsync<string>(
            runId,
            cancellationToken: TestContext.Current.CancellationToken);
        await wait.Should().ThrowAsync<OperationCanceledException>();
    }

    private WorkflowEngine CreateLoopEngine(
        ControlledLoopWorkflow workflow,
        string name) =>
        new(
            CreateStore(),
            new WorkflowRegistry().Register(name, "1", workflow),
            new ZhinuOptions
            {
                PollInterval = TimeSpan.FromMilliseconds(10),
                LeaseDuration = TimeSpan.FromSeconds(2),
                LeaseRenewalInterval = TimeSpan.FromMilliseconds(600)
            });

    private sealed class ControlledLoopWorkflow(
        bool blockSecondIteration,
        bool observeCancellation) : IWorkflow<string, string>
    {
        private readonly TaskCompletionSource releaseSecondIteration =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource SecondIterationStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int BodyCalls;

        public void ReleaseSecondIteration() =>
            releaseSecondIteration.TrySetResult();

        public async Task<string> RunAsync(
            WorkflowContext context,
            string input,
            CancellationToken cancellationToken)
        {
            var result = await context.LoopAsync(
                "refinement",
                0,
                state => state < 2,
                async (iteration, token) =>
                {
                    var next = await iteration.StepAsync(
                        "advance",
                        iteration.State,
                        async (state, _, stepToken) =>
                        {
                            Interlocked.Increment(ref BodyCalls);
                            if (blockSecondIteration && state == 1)
                            {
                                SecondIterationStarted.TrySetResult();
                                if (observeCancellation)
                                {
                                    await Task.Delay(
                                        Timeout.InfiniteTimeSpan,
                                        stepToken);
                                }
                                else
                                {
                                    await releaseSecondIteration.Task;
                                }
                            }
                            return state + 1;
                        },
                        cancellationToken: token);
                    return iteration.Continue(next);
                },
                new LoopOptions(maxIterations: 3),
                cancellationToken);
            return result.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
