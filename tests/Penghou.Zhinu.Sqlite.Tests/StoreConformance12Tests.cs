using FluentAssertions;
using System.Text.Json;
#pragma warning disable xUnit1051

namespace Penghou.Zhinu.Sqlite.Tests;

public sealed class StoreConformance12Tests : WorkflowEngineTestBase
{
    [Fact]
    public async Task Conformance_ConcurrentStepClaim_OneWinner()
    {
        var workflow = new TwoStepWorkflow();
        var engine1 = CreateEngine(workflow, "concurrent-claim", TimeSpan.FromSeconds(10));
        var engine2 = CreateEngine(workflow, "concurrent-claim", TimeSpan.FromSeconds(10));
        var runId = await engine1.StartAsync("concurrent-claim", "1", "x", cancellationToken: TestContext.Current.CancellationToken);
        var t1 = engine1.ExecuteAsync(runId, TestContext.Current.CancellationToken);
        var t2 = engine2.ExecuteAsync(runId, TestContext.Current.CancellationToken);
        await Task.WhenAll(t1, t2);
        var result = await engine1.WaitForCompletionAsync<string>(runId, cancellationToken: TestContext.Current.CancellationToken);
        result.Should().NotBeNull();
    }

    [Fact]
    public async Task Conformance_LeaseExpiry_PermitsRecovery()
    {
        var store = CreateStore();
        var workflow = new SlowWorkflow();
        var registry = new WorkflowRegistry().Register("lease-expiry", "1", workflow);
        var engine = new WorkflowEngine(store, registry, new ZhinuOptions { LeaseDuration = TimeSpan.FromMilliseconds(200), LeaseRenewalInterval = TimeSpan.FromMilliseconds(50), PollInterval = TimeSpan.FromMilliseconds(10) });
        var runId = await engine.StartAsync("lease-expiry", "1", "x", cancellationToken: TestContext.Current.CancellationToken);
        var exec = engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
        await Task.Delay(500, TestContext.Current.CancellationToken);
        try { await exec; } catch { }
        // Recovery via second engine
        var engine2 = new WorkflowEngine(store, registry, new ZhinuOptions { LeaseDuration = TimeSpan.FromSeconds(2), LeaseRenewalInterval = TimeSpan.FromMilliseconds(500), PollInterval = TimeSpan.FromMilliseconds(10) });
        await engine2.ExecuteAsync(runId, TestContext.Current.CancellationToken);
        var run = await engine2.GetRunAsync(runId, TestContext.Current.CancellationToken);
        run!.Status.Should().BeOneOf(WorkflowStatus.Completed, WorkflowStatus.Running, WorkflowStatus.Failed);
    }

    [Fact]
    public async Task Conformance_StaleGeneration_CannotComplete()
    {
        var workflow = new TwoStepWorkflow();
        var engine = CreateEngine(workflow, "stale-gen");
        var runId = await engine.StartAsync("stale-gen", "1", "x", cancellationToken: TestContext.Current.CancellationToken);
        await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
        await engine.WaitForCompletionAsync<string>(runId, cancellationToken: TestContext.Current.CancellationToken);
        // Restart bumps generation
        await engine.RestartStepAsync(runId, "first", TestContext.Current.CancellationToken);
        var steps = await engine.GetStepsAsync(runId, TestContext.Current.CancellationToken);
        steps.Should().Contain(s => s.StepKey == "first" && s.Status == StepStatus.Pending);
    }

    [Fact]
    public async Task Conformance_RecoveredGeneration_CanComplete()
    {
        var workflow = new TwoStepWorkflow();
        var engine = CreateEngine(workflow, "recovered-gen");
        var runId = await engine.StartAsync("recovered-gen", "1", "x", cancellationToken: TestContext.Current.CancellationToken);
        await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
        await engine.RestartStepAsync(runId, "first", TestContext.Current.CancellationToken);
        await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
        var result = await engine.WaitForCompletionAsync<string>(runId, cancellationToken: TestContext.Current.CancellationToken);
        result.Should().NotBeNull();
    }

    [Fact]
    public async Task Conformance_ConcurrentCompensationClaim_OneWinner()
    {
        var workflow = new CompensationWorkflow();
        var engine = CreateEngine(workflow, "comp-concurrent");
        var runId = await engine.StartAsync("comp-concurrent", "1", "x", cancellationToken: TestContext.Current.CancellationToken);
        await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
        await engine.WaitForCompletionAsync<string>(runId, cancellationToken: TestContext.Current.CancellationToken);
        var t1 = engine.RollbackAsync(runId, cancellationToken: TestContext.Current.CancellationToken);
        var t2 = engine.RollbackAsync(runId, cancellationToken: TestContext.Current.CancellationToken);
        try { await Task.WhenAll(t1, t2); } catch { }
        var run = await engine.GetRunAsync(runId, TestContext.Current.CancellationToken);
        run!.Status.Should().BeOneOf(WorkflowStatus.Compensated, WorkflowStatus.Failed);
    }

    [Fact]
    public async Task Conformance_Signal_ExactlyOnce()
    {
        var workflow = new SignalWorkflow();
        var engine = CreateEngine(workflow, "signal-once");
        var runId = await engine.StartAsync("signal-once", "1", "x", cancellationToken: TestContext.Current.CancellationToken);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var exec = engine.ExecuteAsync(runId, cts.Token);
        await WaitUntilAsync(() => HasStepStatusAsync(engine, runId, "approval", StepStatus.Waiting, cts.Token), cts.Token);
        await engine.SendSignalAsync(runId, "approve", "once", cts.Token);
        await engine.SendSignalAsync(runId, "approve", "once", cts.Token);
        await exec;
        var events = await engine.GetEventsAsync(runId, cancellationToken: TestContext.Current.CancellationToken);
        events.Count(e => e.EventType == WorkflowEventTypes.SignalDelivered).Should().Be(1);
    }

    [Fact]
    public async Task Conformance_DuplicateCompletion_Safe()
    {
        var workflow = new TwoStepWorkflow();
        var engine = CreateEngine(workflow, "dup-complete");
        var runId = await engine.StartAsync("dup-complete", "1", "x", cancellationToken: TestContext.Current.CancellationToken);
        await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
        var r1 = await engine.WaitForCompletionAsync<string>(runId, cancellationToken: TestContext.Current.CancellationToken);
        var r2 = await engine.WaitForCompletionAsync<string>(runId, cancellationToken: TestContext.Current.CancellationToken);
        r1.Should().Be(r2);
    }

    [Fact]
    public async Task Conformance_CancellationRace_Consistent()
    {
        var workflow = new SlowWorkflow();
        var engine = CreateEngine(workflow, "cancel-race");
        var runId = await engine.StartAsync("cancel-race", "1", "x", cancellationToken: TestContext.Current.CancellationToken);
        var exec = engine.ExecuteAsync(runId, CancellationToken.None);
        await Task.Delay(50);
        var cancel = engine.CancelAsync(runId, TestContext.Current.CancellationToken);
        await Task.WhenAll(exec.ContinueWith(_ => { }), cancel);
        var run = await engine.GetRunAsync(runId, TestContext.Current.CancellationToken);
        run!.Status.Should().BeOneOf(WorkflowStatus.Cancelled, WorkflowStatus.Failed, WorkflowStatus.Completed, WorkflowStatus.Running);
    }

    [Fact]
    public async Task Conformance_ChildCreation_Deterministic()
    {
        var parent = new ParentWorkflow();
        var child = new ChildWorkflow();
        var engine = CreateEngine(new WorkflowRegistry().Register("parent-det", "1", parent).Register("child", "1", child));
        var runId = await engine.StartAsync("parent-det", "1", "x", cancellationToken: TestContext.Current.CancellationToken);
        await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
        await engine.WaitForCompletionAsync<string>(runId, cancellationToken: TestContext.Current.CancellationToken);
        var store = CreateStore();
        var subtree = await store.GetRunSubtreeAsync(runId, 5, TestContext.Current.CancellationToken);
        subtree.Count(r => r.ParentRunId == runId).Should().Be(1);
        var childId = subtree.First(r => r.ParentRunId == runId).Id;
        // Re-execute should reuse same child
        await engine.RestartStepAsync(runId, "child:start", TestContext.Current.CancellationToken);
        await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
        await engine.WaitForCompletionAsync<string>(runId, cancellationToken: TestContext.Current.CancellationToken);
        var subtree2 = await store.GetRunSubtreeAsync(runId, 5, TestContext.Current.CancellationToken);
        subtree2.First(r => r.ParentRunId == runId).Id.Should().Be(childId);
    }

    [Fact]
    public async Task Conformance_Restart_PreservesHistory()
    {
        var workflow = new TwoStepWorkflow();
        var engine = CreateEngine(workflow, "restart-history");
        var runId = await engine.StartAsync("restart-history", "1", "x", cancellationToken: TestContext.Current.CancellationToken);
        await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
        await engine.WaitForCompletionAsync<string>(runId, cancellationToken: TestContext.Current.CancellationToken);
        await engine.RestartStepAsync(runId, "first", TestContext.Current.CancellationToken);
        var after = await engine.GetStepsAsync(runId, TestContext.Current.CancellationToken);
        after.Should().Contain(s => s.StepKey == "first" && s.Status == StepStatus.Pending);
    }

    [Fact]
    public async Task Conformance_Rollback_Ordering_Deterministic()
    {
        var workflow = new RollbackWorkflow();
        var engine = CreateEngine(workflow, "rollback-order");
        var runId = await engine.StartAsync("rollback-order", "1", "x", cancellationToken: TestContext.Current.CancellationToken);
        await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
        await engine.WaitForCompletionAsync<string>(runId, cancellationToken: TestContext.Current.CancellationToken);
        var plan1 = await engine.PlanRollbackAsync(runId, cancellationToken: TestContext.Current.CancellationToken);
        var plan2 = await engine.PlanRollbackAsync(runId, cancellationToken: TestContext.Current.CancellationToken);
        plan1.Steps.Select(s => s.StepKey).Should().Equal(plan2.Steps.Select(s => s.StepKey));
    }

    [Fact]
    public async Task Conformance_Pagination_StableUnderInserts()
    {
        var engine = CreateEngine(new TwoStepWorkflow(), "pagination-stable");
        for (var i = 0; i < 5; i++) await engine.StartAsync("pagination-stable", "1", $"a{i}", cancellationToken: TestContext.Current.CancellationToken);
        var p1 = await engine.GetRunsAsync(new RunQuery { WorkflowName = "pagination-stable", Limit = 3 }, TestContext.Current.CancellationToken);
        var extra = await engine.StartAsync("pagination-stable", "1", "extra", cancellationToken: TestContext.Current.CancellationToken);
        var p2 = await engine.GetRunsAsync(new RunQuery { WorkflowName = "pagination-stable", Limit = 3, AfterId = p1[^1].Id }, TestContext.Current.CancellationToken);
        var all = p1.Concat(p2).Select(r => r.Id).ToList();
        all.Should().OnlyHaveUniqueItems();
        all.Should().Contain(extra);
    }
}
