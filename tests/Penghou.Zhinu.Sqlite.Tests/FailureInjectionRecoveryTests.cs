using FluentAssertions;
#pragma warning disable xUnit1051

namespace Penghou.Zhinu.Sqlite.Tests;

public sealed class FailureInjectionRecoveryTests : WorkflowEngineTestBase
{
    private FaultInjectingWorkflowStore CreateFaultStore() =>
        new(CreateStore());

    private static WorkflowEngine CreateEngine(
        IWorkflowStore store,
        IWorkflow<string, string> workflow,
        string name)
    {
        var registry = new WorkflowRegistry().Register(name, "1", workflow);
        return new WorkflowEngine(
            store,
            registry,
            new ZhinuOptions
            {
                PollInterval = TimeSpan.FromMilliseconds(10),
                LeaseDuration = TimeSpan.FromSeconds(2),
                LeaseRenewalInterval = TimeSpan.FromMilliseconds(600)
            });
    }

    private static Func<Task> Execute(WorkflowEngine engine, Guid runId) =>
        () => engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);

    [Fact]
    public async Task Recover_AfterClaimPersisted_StepStaysRunningThenRestartCompletes()
    {
        var store = CreateFaultStore();
        var workflow = new TwoStepWorkflow();
        var engine = CreateEngine(store, workflow, "fault-after-claim");
        store.Arm(FaultInjectingWorkflowStore.AfterClaimPersisted);
        var runId = await engine.StartAsync("fault-after-claim", "1", "x", cancellationToken: TestContext.Current.CancellationToken);
        // The injected failure is converted into a durable run failure; it does not surface.
        await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);

        // The step claim committed (Running + lease held) but the run was failed by the engine.
        var steps = await store.GetStepsAsync(runId, TestContext.Current.CancellationToken);
        steps.Should().Contain(s => s.StepKey == "first" && s.Status == StepStatus.Running);
        var run = await store.GetRunAsync(runId, TestContext.Current.CancellationToken);
        run!.Status.Should().Be(WorkflowStatus.Failed);

        // Fresh engine (new owner) recovers via restart of the interrupted step.
        store.Reset();
        var recovery = CreateEngine(store, workflow, "fault-after-claim");
        await recovery.RestartStepAsync(runId, "first", TestContext.Current.CancellationToken);
        await recovery.ExecuteAsync(runId, TestContext.Current.CancellationToken);
        var result = await recovery.WaitForCompletionAsync<string>(runId, cancellationToken: TestContext.Current.CancellationToken);
        result.Should().Be("Hello, x!");
        var finalSteps = await store.GetStepsAsync(runId, TestContext.Current.CancellationToken);
        finalSteps.Should().Contain(s => s.StepKey == "first" && s.Status == StepStatus.Completed);
    }

    [Fact]
    public async Task Recover_BeforeStepCompletionCommit_StepNotCommittedThenRestartReruns()
    {
        var store = CreateFaultStore();
        var workflow = new TwoStepWorkflow();
        var engine = CreateEngine(store, workflow, "fault-before-commit");
        store.Arm(FaultInjectingWorkflowStore.BeforeStepCompletionCommit);
        var runId = await engine.StartAsync("fault-before-commit", "1", "x", cancellationToken: TestContext.Current.CancellationToken);
        await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);

        // Completion did not commit: the step is failed with no committed output.
        var steps = await store.GetStepsAsync(runId, TestContext.Current.CancellationToken);
        var first = steps.Single(s => s.StepKey == "first");
        first.Status.Should().Be(StepStatus.Failed);
        first.OutputJson.Should().BeNull();

        store.Reset();
        var recovery = CreateEngine(store, workflow, "fault-before-commit");
        await recovery.RestartStepAsync(runId, "first", TestContext.Current.CancellationToken);
        await recovery.ExecuteAsync(runId, TestContext.Current.CancellationToken);
        var result = await recovery.WaitForCompletionAsync<string>(runId, cancellationToken: TestContext.Current.CancellationToken);
        result.Should().Be("Hello, x!");
    }

    [Fact]
    public async Task Recover_AfterStepCompletionCommit_CommittedStepIsReusedNotReexecuted()
    {
        var store = CreateFaultStore();
        var workflow = new TwoStepWorkflow();
        var engine = CreateEngine(store, workflow, "fault-after-commit");
        // Fire after the SECOND step's completion commit so the first step is durably committed
        // and the second step exists to be restarted during recovery.
        store.Arm(FaultInjectingWorkflowStore.AfterStepCompletionCommit, count: 2);
        var runId = await engine.StartAsync("fault-after-commit", "1", "x", cancellationToken: TestContext.Current.CancellationToken);
        await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);

        // The committed step's output survived the crash.
        var steps = await store.GetStepsAsync(runId, TestContext.Current.CancellationToken);
        var committed = steps.Single(s => s.StepKey == "first" && s.Status == StepStatus.Completed);
        committed.OutputJson.Should().NotBeNull();
        workflow.FirstCalls.Should().Be(1);

        // Fresh engine recovers: restarting the second step reuses the committed first step
        // (delegate NOT re-run) and re-runs only the second.
        store.Reset();
        var recovery = CreateEngine(store, workflow, "fault-after-commit");
        await recovery.RestartStepAsync(runId, "second", TestContext.Current.CancellationToken);
        await recovery.ExecuteAsync(runId, TestContext.Current.CancellationToken);
        var result = await recovery.WaitForCompletionAsync<string>(runId, cancellationToken: TestContext.Current.CancellationToken);
        result.Should().Be("Hello, x!");
        workflow.FirstCalls.Should().Be(1);
    }

    [Fact]
    public async Task Recover_BeforeCompensationCommit_CompensationRetriedOnNextRollback()
    {
        var store = CreateFaultStore();
        var workflow = new CompensationWorkflow();
        var engine = CreateEngine(store, workflow, "fault-before-comp");
        store.Arm(FaultInjectingWorkflowStore.BeforeCompensationCommit);
        var runId = await engine.StartAsync("fault-before-comp", "1", "x", cancellationToken: TestContext.Current.CancellationToken);
        await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
        await engine.WaitForCompletionAsync<string>(runId, cancellationToken: TestContext.Current.CancellationToken);

        // The compensation commit failed: the rollback surfaces RollbackFailedException and the
        // compensation is not Completed.
        var rollback = () => engine.RollbackAsync(runId, cancellationToken: TestContext.Current.CancellationToken);
        await rollback.Should().ThrowAsync<RollbackFailedException>();
        var compensations = await store.GetCompensationsAsync(runId, TestContext.Current.CancellationToken);
        compensations.Should().Contain(c => c.StepKey == "reserve" && c.Status != CompensationStatus.Completed);
        var run = await store.GetRunAsync(runId, TestContext.Current.CancellationToken);
        run!.Status.Should().Be(WorkflowStatus.Failed);

        // Fresh engine retries the failed compensation to completion.
        store.Reset();
        var recovery = CreateEngine(store, workflow, "fault-before-comp");
        await recovery.RollbackAsync(runId, cancellationToken: TestContext.Current.CancellationToken);
        run = await store.GetRunAsync(runId, TestContext.Current.CancellationToken);
        run!.Status.Should().Be(WorkflowStatus.Compensated);
        var finalCompensations = await store.GetCompensationsAsync(runId, TestContext.Current.CancellationToken);
        finalCompensations.Should().Contain(c => c.StepKey == "reserve" && c.Status == CompensationStatus.Completed);
    }

    [Fact]
    public async Task Recover_AfterCompensationClaim_CompensationRetriedOnNextRollback()
    {
        var store = CreateFaultStore();
        var workflow = new CompensationWorkflow();
        var engine = CreateEngine(store, workflow, "fault-after-comp-claim");
        store.Arm(FaultInjectingWorkflowStore.AfterCompensationClaim);
        var runId = await engine.StartAsync("fault-after-comp-claim", "1", "x", cancellationToken: TestContext.Current.CancellationToken);
        await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
        await engine.WaitForCompletionAsync<string>(runId, cancellationToken: TestContext.Current.CancellationToken);

        var rollback = () => engine.RollbackAsync(runId, cancellationToken: TestContext.Current.CancellationToken);
        await rollback.Should().ThrowAsync<FaultInjectedException>();

        store.Reset();
        var recovery = CreateEngine(store, workflow, "fault-after-comp-claim");
        await recovery.RollbackAsync(runId, cancellationToken: TestContext.Current.CancellationToken);
        var run = await store.GetRunAsync(runId, TestContext.Current.CancellationToken);
        run!.Status.Should().Be(WorkflowStatus.Compensated);
    }

    [Fact]
    public async Task Recover_BeforeRestartCommit_LeavesStateIntact()
    {
        var store = CreateFaultStore();
        var workflow = new TwoStepWorkflow();
        var engine = CreateEngine(store, workflow, "fault-before-restart");
        var runId = await engine.StartAsync("fault-before-restart", "1", "x", cancellationToken: TestContext.Current.CancellationToken);
        await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
        await engine.WaitForCompletionAsync<string>(runId, cancellationToken: TestContext.Current.CancellationToken);
        var before = await store.GetStepsAsync(runId, TestContext.Current.CancellationToken);

        store.Arm(FaultInjectingWorkflowStore.BeforeRestartCommit);
        var restart = () => engine.RestartStepAsync(runId, "first", TestContext.Current.CancellationToken);
        await restart.Should().ThrowAsync<FaultInjectedException>();

        // Restart did not partially apply: no new revision, run still terminal.
        var after = await store.GetStepsAsync(runId, TestContext.Current.CancellationToken);
        after.Count.Should().Be(before.Count);
        var run = await store.GetRunAsync(runId, TestContext.Current.CancellationToken);
        run!.Status.Should().Be(WorkflowStatus.Completed);

        // A retry applies cleanly.
        store.Reset();
        var recovery = CreateEngine(store, workflow, "fault-before-restart");
        await recovery.RestartStepAsync(runId, "first", TestContext.Current.CancellationToken);
        var plan = await recovery.PlanRestartAsync(runId, "first", cancellationToken: TestContext.Current.CancellationToken);
        plan.StepsToInvalidate.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Recover_BeforeRollbackTransition_LeavesRunClaimable()
    {
        var store = CreateFaultStore();
        var workflow = new CompensationWorkflow();
        var engine = CreateEngine(store, workflow, "fault-before-rollback");
        var runId = await engine.StartAsync("fault-before-rollback", "1", "x", cancellationToken: TestContext.Current.CancellationToken);
        await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
        await engine.WaitForCompletionAsync<string>(runId, cancellationToken: TestContext.Current.CancellationToken);

        store.Arm(FaultInjectingWorkflowStore.BeforeRollbackTransition);
        var rollback = () => engine.RollbackAsync(runId, cancellationToken: TestContext.Current.CancellationToken);
        await rollback.Should().ThrowAsync<FaultInjectedException>();
        var run = await store.GetRunAsync(runId, TestContext.Current.CancellationToken);
        run!.Status.Should().NotBe(WorkflowStatus.Compensated);

        store.Reset();
        var recovery = CreateEngine(store, workflow, "fault-before-rollback");
        await recovery.RollbackAsync(runId, cancellationToken: TestContext.Current.CancellationToken);
        run = await store.GetRunAsync(runId, TestContext.Current.CancellationToken);
        run!.Status.Should().Be(WorkflowStatus.Compensated);
    }
}
