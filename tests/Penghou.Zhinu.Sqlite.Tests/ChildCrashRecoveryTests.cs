using FluentAssertions;
#pragma warning disable xUnit1051

namespace Penghou.Zhinu.Sqlite.Tests;

public sealed class ChildCrashRecoveryTests : WorkflowEngineTestBase
{
    [Fact]
    public async Task Child_DeterministicIdentity_SameParentStepSameChildId()
    {
        var parent = new ParentWorkflow();
        var child = new ChildWorkflow();
        var engine = CreateEngine(new WorkflowRegistry().Register("parent", "1", parent).Register("child", "1", child));
        var runId = await engine.StartAsync("parent", "1", "x", cancellationToken: TestContext.Current.CancellationToken);
        await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
        await engine.WaitForCompletionAsync<string>(runId, cancellationToken: TestContext.Current.CancellationToken);
        var store = CreateStore();
        var subtree = await store.GetRunSubtreeAsync(runId, 5, TestContext.Current.CancellationToken);
        subtree.Should().Contain(r => r.ParentRunId == runId);
        var childRun = subtree.First(r => r.ParentRunId == runId);
        // Restart parent's child:start step - creates a fresh child (new invocation generation)
        await engine.RestartStepAsync(runId, "child:start", TestContext.Current.CancellationToken);
        await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
        await engine.WaitForCompletionAsync<string>(runId, cancellationToken: TestContext.Current.CancellationToken);
        var subtree2 = await store.GetRunSubtreeAsync(runId, 5, TestContext.Current.CancellationToken);
        var children = subtree2.Where(r => r.ParentRunId == runId).ToList();
        children.Should().HaveCount(2);
        children.Should().ContainSingle(r => r.Id == childRun.Id);
    }

    [Fact]
    public async Task Child_Cancellation_PropagatesToChild()
    {
        var parent = new ParentWorkflow();
        var child = new SlowChildWorkflow();
        var engine = CreateEngine(new WorkflowRegistry().Register("parent-slow", "1", parent).Register("child", "1", child));
        var runId = await engine.StartAsync("parent-slow", "1", "x", cancellationToken: TestContext.Current.CancellationToken);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var exec = engine.ExecuteAsync(runId, cts.Token);
        await WaitUntilAsync(() => HasStepStatusAsync(engine, runId, "child:wait", StepStatus.Running, cts.Token), cts.Token);
        await engine.CancelAsync(runId, TestContext.Current.CancellationToken);
        await exec;
        var store = CreateStore();
        var childRuns = await store.GetRunSubtreeAsync(runId, 5, TestContext.Current.CancellationToken);
        childRuns.Should().HaveCount(2)
            .And.OnlyContain(run => run.Status == WorkflowStatus.Cancelled);
        foreach (var run in childRuns)
        {
            var events = await store.GetEventsAsync(
                run.Id,
                0,
                100,
                TestContext.Current.CancellationToken);
            events.Count(item =>
                item.EventType == WorkflowEventTypes.WorkflowCancelled)
                .Should().Be(1);
        }
    }

    [Fact]
    public async Task Child_NestingDepth_Enforced()
    {
        var opts = new ZhinuOptions { MaxNestingDepth = 2 };
        var registry = new WorkflowRegistry().Register("parent", "1", new ParentWorkflow()).Register("child", "1", new ChildWorkflow());
        var engine = new WorkflowEngine(CreateStore(), registry, opts);
        var runId = await engine.StartAsync("parent", "1", "x", cancellationToken: TestContext.Current.CancellationToken);
        await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
        var result = await engine.WaitForCompletionAsync<string>(runId, cancellationToken: TestContext.Current.CancellationToken);
        result.Should().NotBeNull();
    }

    [Fact]
    public async Task Child_AlreadyTerminal_PropagatesFailure()
    {
        var engine = CreateEngine(new WorkflowRegistry().Register("parent-fail", "1", new FailingParentWorkflow()).Register("bad-child", "1", new FailingChildWorkflow()));
        var runId = await engine.StartAsync("parent-fail", "1", "x", cancellationToken: TestContext.Current.CancellationToken);
        await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
        var act = () => engine.WaitForCompletionAsync<string>(runId, cancellationToken: TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<WorkflowExecutionFailedException>();
    }

    private sealed class SlowChildWorkflow : IWorkflow<string, string>
    {
        public async Task<string> RunAsync(WorkflowContext ctx, string input, CancellationToken ct)
        {
            await Task.Delay(2000, ct);
            return await ctx.StepAsync("child-step", input, (v, _) => Task.FromResult($"child:{v}"), cancellationToken: ct);
        }
    }
}
