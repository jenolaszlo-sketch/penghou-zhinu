using FluentAssertions;

namespace Penghou.Zhinu.Sqlite.Tests;

public sealed class DurableLoopRecoveryTests : WorkflowEngineTestBase
{
    [Fact]
    public async Task BodyStep_InterruptedBeforeCommit_RerunsWithoutAdvancingLoop()
    {
        var store = new FaultInjectingWorkflowStore(CreateStore());
        var first = new ContinueOnceWorkflow();
        var engine = CreateEngine(store, first, "loop-before-body-commit");
        using var interruption = new CancellationTokenSource();
        store.ArmInterruption(
            FaultInjectingWorkflowStore.BeforeStepCompletionCommit,
            interruption,
            count: 2);
        var runId = await engine.StartAsync(
            "loop-before-body-commit",
            "1",
            "ignored",
            cancellationToken: TestContext.Current.CancellationToken);

        await engine.ExecuteAsync(runId, interruption.Token);

        var interruptedEvents = await store.GetEventsAsync(
            runId,
            0,
            100,
            TestContext.Current.CancellationToken);
        interruptedEvents.Should().NotContain(item =>
            item.EventType == WorkflowEventTypes.LoopIterationCommitted);

        store.Reset();
        var recoveredAt = DateTimeOffset.UtcNow.AddDays(1);
        await store.RecoverExpiredLeasesAsync(
            recoveredAt,
            TestContext.Current.CancellationToken);
        var resumed = new ContinueOnceWorkflow();
        var recovery = CreateEngine(
            store,
            resumed,
            "loop-before-body-commit",
            new FixedTimeProvider(recoveredAt));
        await recovery.ExecuteAsync(runId, TestContext.Current.CancellationToken);
        var result = await recovery.WaitForCompletionAsync<string>(
            runId,
            cancellationToken: TestContext.Current.CancellationToken);

        result.Should().Be("1");
        first.BodyCalls.Should().Be(1);
        resumed.BodyCalls.Should().Be(1);
        var events = await store.GetEventsAsync(
            runId,
            0,
            100,
            TestContext.Current.CancellationToken);
        events.Count(item =>
            item.EventType == WorkflowEventTypes.LoopIterationCommitted)
            .Should().Be(1);
    }

    [Fact]
    public async Task ContinueCommit_InterruptedBeforeCommit_ReusesBodyAndCommitsOnce()
    {
        var store = new FaultInjectingWorkflowStore(CreateStore());
        var first = new ContinueOnceWorkflow();
        var engine = CreateEngine(store, first, "loop-before-continue-commit");
        using var interruption = new CancellationTokenSource();
        store.ArmInterruption(
            FaultInjectingWorkflowStore.BeforeStepCompletionCommit,
            interruption,
            count: 3);
        var runId = await engine.StartAsync(
            "loop-before-continue-commit",
            "1",
            "ignored",
            cancellationToken: TestContext.Current.CancellationToken);

        await engine.ExecuteAsync(runId, interruption.Token);

        first.BodyCalls.Should().Be(1);
        var interruptedSteps = await store.GetStepsAsync(
            runId,
            TestContext.Current.CancellationToken);
        interruptedSteps.Single(step =>
                step.StepKey == "$loop/refinement/1/commit")
            .Status.Should().Be(StepStatus.Running);

        store.Reset();
        var recoveredAt = DateTimeOffset.UtcNow.AddDays(1);
        await store.RecoverExpiredLeasesAsync(
            recoveredAt,
            TestContext.Current.CancellationToken);
        var resumed = new ContinueOnceWorkflow();
        var recovery = CreateEngine(
            store,
            resumed,
            "loop-before-continue-commit",
            new FixedTimeProvider(recoveredAt));
        await recovery.ExecuteAsync(runId, TestContext.Current.CancellationToken);
        var result = await recovery.WaitForCompletionAsync<string>(
            runId,
            cancellationToken: TestContext.Current.CancellationToken);

        result.Should().Be("1");
        resumed.BodyCalls.Should().Be(0);
        var events = await store.GetEventsAsync(
            runId,
            0,
            100,
            TestContext.Current.CancellationToken);
        events.Count(item =>
            item.EventType == WorkflowEventTypes.LoopIterationCommitted)
            .Should().Be(1);
    }

    [Fact]
    public async Task ContinueCommit_InterruptedAfterCommit_ReusesDisposition()
    {
        var store = new FaultInjectingWorkflowStore(CreateStore());
        var first = new ContinueOnceWorkflow();
        var engine = CreateEngine(store, first, "loop-after-continue-commit");
        using var interruption = new CancellationTokenSource();
        store.ArmInterruption(
            FaultInjectingWorkflowStore.AfterStepCompletionCommit,
            interruption,
            count: 3);
        var runId = await engine.StartAsync(
            "loop-after-continue-commit",
            "1",
            "ignored",
            cancellationToken: TestContext.Current.CancellationToken);

        await engine.ExecuteAsync(runId, interruption.Token);

        var committed = (await store.GetStepsAsync(
                runId,
                TestContext.Current.CancellationToken))
            .Single(step => step.StepKey == "$loop/refinement/1/commit");
        committed.Status.Should().Be(StepStatus.Completed);

        store.Reset();
        var resumed = new ContinueOnceWorkflow();
        var recovery = CreateEngine(
            store,
            resumed,
            "loop-after-continue-commit");
        await recovery.ExecuteAsync(runId, TestContext.Current.CancellationToken);
        var result = await recovery.WaitForCompletionAsync<string>(
            runId,
            cancellationToken: TestContext.Current.CancellationToken);

        result.Should().Be("1");
        resumed.BodyCalls.Should().Be(0);
        resumed.ConditionCalls.Should().Be(1);
        var events = await store.GetEventsAsync(
            runId,
            0,
            100,
            TestContext.Current.CancellationToken);
        events.Count(item =>
            item.EventType == WorkflowEventTypes.LoopIterationCommitted)
            .Should().Be(1);
    }

    [Fact]
    public async Task BreakCommit_InterruptedAfterCommit_CompletesWithoutBodyOrCondition()
    {
        var store = new FaultInjectingWorkflowStore(CreateStore());
        var first = new BreakImmediatelyWorkflow();
        var engine = CreateEngine(store, first, "loop-after-break-commit");
        using var interruption = new CancellationTokenSource();
        store.ArmInterruption(
            FaultInjectingWorkflowStore.AfterStepCompletionCommit,
            interruption,
            count: 3);
        var runId = await engine.StartAsync(
            "loop-after-break-commit",
            "1",
            "ignored",
            cancellationToken: TestContext.Current.CancellationToken);

        await engine.ExecuteAsync(runId, interruption.Token);

        store.Reset();
        var resumed = new BreakImmediatelyWorkflow();
        var recovery = CreateEngine(
            store,
            resumed,
            "loop-after-break-commit");
        await recovery.ExecuteAsync(runId, TestContext.Current.CancellationToken);
        var result = await recovery.WaitForCompletionAsync<string>(
            runId,
            cancellationToken: TestContext.Current.CancellationToken);

        result.Should().Be("1");
        resumed.BodyCalls.Should().Be(0);
        resumed.ConditionCalls.Should().Be(0);
        var events = await store.GetEventsAsync(
            runId,
            0,
            100,
            TestContext.Current.CancellationToken);
        events.Count(item =>
            item.EventType == WorkflowEventTypes.LoopIterationCommitted)
            .Should().Be(1);
        events.Count(item => item.EventType == WorkflowEventTypes.LoopCompleted)
            .Should().Be(1);
    }

    [Fact]
    public async Task ContinueCommit_LostGeneration_RejectsStaleCommitAndRecovers()
    {
        var store = new FaultInjectingWorkflowStore(CreateStore());
        var first = new ContinueOnceWorkflow();
        var engine = CreateEngine(store, first, "loop-stale-commit");
        var runId = await engine.StartAsync(
            "loop-stale-commit",
            "1",
            "ignored",
            cancellationToken: TestContext.Current.CancellationToken);
        store.ArmCallback(
            FaultInjectingWorkflowStore.BeforeStepCompletionCommit,
            () => store.RestartStepAsync(
                    runId,
                    "$loop/refinement/1/commit",
                    StepRestartMode.Dependents,
                    "test",
                    "advance generation before stale commit",
                    DateTimeOffset.UtcNow,
                    TestContext.Current.CancellationToken)
                .AsTask()
                .GetAwaiter()
                .GetResult(),
            count: 3);

        await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);

        var interruptedEvents = await store.GetEventsAsync(
            runId,
            0,
            100,
            TestContext.Current.CancellationToken);
        interruptedEvents.Should().NotContain(item =>
            item.EventType == WorkflowEventTypes.LoopIterationCommitted);

        store.Reset();
        var resumed = new ContinueOnceWorkflow();
        var recovery = CreateEngine(store, resumed, "loop-stale-commit");
        await recovery.ExecuteAsync(runId, TestContext.Current.CancellationToken);
        var result = await recovery.WaitForCompletionAsync<string>(
            runId,
            cancellationToken: TestContext.Current.CancellationToken);

        result.Should().Be("1");
        resumed.BodyCalls.Should().Be(0);
        var events = await store.GetEventsAsync(
            runId,
            0,
            100,
            TestContext.Current.CancellationToken);
        events.Count(item =>
            item.EventType == WorkflowEventTypes.LoopIterationCommitted)
            .Should().Be(1);
    }

    [Fact]
    public async Task LoopCompensation_InterruptedBeforeCommit_RetriesWithStableKey()
    {
        var store = new FaultInjectingWorkflowStore(CreateStore());
        var forward = new CompensatingLoopWorkflow();
        var engine = CreateEngine(store, forward, "loop-compensation-recovery");
        var runId = await engine.StartAsync(
            "loop-compensation-recovery",
            "1",
            "ignored",
            cancellationToken: TestContext.Current.CancellationToken);
        await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
        await engine.WaitForCompletionAsync<string>(
            runId,
            cancellationToken: TestContext.Current.CancellationToken);

        store.Arm(FaultInjectingWorkflowStore.BeforeCompensationCommit);
        var firstRollback = new CompensatingLoopWorkflow();
        var rollbackEngine = CreateEngine(
            store,
            firstRollback,
            "loop-compensation-recovery");
        var rollback = () => rollbackEngine.RollbackAsync(
            runId,
            cancellationToken: TestContext.Current.CancellationToken);
        await rollback.Should().ThrowAsync<RollbackFailedException>();
        firstRollback.CompensationKeys.Should().ContainSingle();

        store.Reset();
        var resumedRollback = new CompensatingLoopWorkflow();
        var recovery = CreateEngine(
            store,
            resumedRollback,
            "loop-compensation-recovery");
        await recovery.RollbackAsync(
            runId,
            cancellationToken: TestContext.Current.CancellationToken);

        resumedRollback.CompensationKeys.Should().ContainSingle()
            .Which.Should().Be(firstRollback.CompensationKeys.Single());
        (await recovery.GetRunAsync(
            runId,
            TestContext.Current.CancellationToken))!.Status
            .Should().Be(WorkflowStatus.Compensated);
    }

    private static WorkflowEngine CreateEngine(
        IWorkflowStore store,
        IWorkflow<string, string> workflow,
        string name,
        TimeProvider? timeProvider = null) =>
        new(
            store,
            new WorkflowRegistry().Register(name, "1", workflow),
            new ZhinuOptions
            {
                PollInterval = TimeSpan.FromMilliseconds(10),
                LeaseDuration = TimeSpan.FromSeconds(2),
                LeaseRenewalInterval = TimeSpan.FromMilliseconds(600)
            },
            timeProvider: timeProvider);

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class ContinueOnceWorkflow : IWorkflow<string, string>
    {
        public int BodyCalls;
        public int ConditionCalls;

        public async Task<string> RunAsync(
            WorkflowContext context,
            string input,
            CancellationToken cancellationToken)
        {
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
                            Interlocked.Increment(ref BodyCalls);
                            return Task.FromResult(state + 1);
                        },
                        cancellationToken: token);
                    return iteration.Continue(next);
                },
                new LoopOptions(maxIterations: 2),
                cancellationToken);
            return result.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    private sealed class BreakImmediatelyWorkflow : IWorkflow<string, string>
    {
        public int BodyCalls;
        public int ConditionCalls;

        public async Task<string> RunAsync(
            WorkflowContext context,
            string input,
            CancellationToken cancellationToken)
        {
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
                    var next = await iteration.StepAsync(
                        "advance",
                        iteration.State,
                        (state, _, _) =>
                        {
                            Interlocked.Increment(ref BodyCalls);
                            return Task.FromResult(state + 1);
                        },
                        cancellationToken: token);
                    return iteration.Break(next);
                },
                new LoopOptions(maxIterations: 2),
                cancellationToken);
            return result.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    private sealed class CompensatingLoopWorkflow : IWorkflow<string, string>
    {
        public List<string> CompensationKeys { get; } = [];

        public async Task<string> RunAsync(
            WorkflowContext context,
            string input,
            CancellationToken cancellationToken)
        {
            var result = await context.LoopAsync(
                "refinement",
                0,
                state => state < 1,
                async (iteration, token) =>
                {
                    var next = await iteration.StepAsync(
                        "advance",
                        iteration.State,
                        (state, _, _) => Task.FromResult(state + 1),
                        cancellationToken: token,
                        compensation: (_, step, _) =>
                        {
                            CompensationKeys.Add(step.IdempotencyKey);
                            return Task.CompletedTask;
                        });
                    return iteration.Continue(next);
                },
                new LoopOptions(maxIterations: 2),
                cancellationToken);
            return $"{input}:{result}";
        }
    }
}
