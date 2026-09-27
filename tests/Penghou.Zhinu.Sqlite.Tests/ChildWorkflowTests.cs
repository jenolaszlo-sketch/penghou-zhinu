using FluentAssertions;
using Penghou.Zhinu.Declarative;
using System.Text.Json;

namespace Penghou.Zhinu.Sqlite.Tests;

public sealed class ChildWorkflowTests : WorkflowEngineTestBase
{

    [Fact]
    public async Task StartChildAsync_ExecutesChildAndWaitsForResult()
    {
        var parent = new ParentWorkflow();
        var child = new ChildWorkflow();
        var engine = CreateEngine(
            new WorkflowRegistry()
                .Register("parent", "1", parent)
                .Register("child", "1", child));
        var runId = await engine.StartAsync(
            "parent",
            "1",
            "go",
            cancellationToken: TestContext.Current.CancellationToken);

        await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
        var result = await engine.WaitForCompletionAsync<string>(
            runId,
            cancellationToken: TestContext.Current.CancellationToken);

        result.Should().Be("child:parent:go");
        child.Calls.Should().Be(1);
        var childRun = (await engine.GetRunsAsync(
            new RunQuery { WorkflowName = "child" },
            cancellationToken: TestContext.Current.CancellationToken)).Single();
        childRun.Status.Should().Be(WorkflowStatus.Completed);
        childRun.ParentRunId.Should().Be(runId);
        childRun.TraceId.Should().Be((await engine.GetRunAsync(
            runId,
            TestContext.Current.CancellationToken))!.TraceId);
        (await engine.GetStepsAsync(
            runId,
            TestContext.Current.CancellationToken)).Should().HaveCount(3)
            .And.OnlyContain(step => step.Status == StepStatus.Completed);
    }

    [Fact]
    public async Task StartChildAsync_ChildFailure_PropagatesToParent()
    {
        var parent = new FailingParentWorkflow();
        var child = new FailingChildWorkflow();
        var engine = CreateEngine(
            new WorkflowRegistry()
                .Register("fail-parent", "1", parent)
                .Register("bad-child", "1", child));
        var runId = await engine.StartAsync(
            "fail-parent",
            "1",
            "x",
            cancellationToken: TestContext.Current.CancellationToken);

        await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);

        var action = () => engine.WaitForCompletionAsync<string>(
            runId,
            cancellationToken: TestContext.Current.CancellationToken);
        await action.Should().ThrowAsync<WorkflowExecutionFailedException>()
            .WithMessage("*child failed*");
        (await engine.GetRunsAsync(
            new RunQuery { WorkflowName = "bad-child" },
            cancellationToken: TestContext.Current.CancellationToken)).Single()
            .Status.Should().Be(WorkflowStatus.Failed);
        (await engine.GetRunAsync(
            runId,
            TestContext.Current.CancellationToken))!.Status.Should().Be(WorkflowStatus.Failed);
    }

    [Fact]
    public async Task StartChildAsync_RestartingStartStepCreatesFreshChildRun()
    {
        var parent = new ParentWorkflow();
        var child = new ChildWorkflow();
        var engine = CreateEngine(
            new WorkflowRegistry()
                .Register("parent", "1", parent)
                .Register("child", "1", child));
        var runId = await engine.StartAsync(
            "parent",
            "1",
            "go",
            cancellationToken: TestContext.Current.CancellationToken);
        await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
        var original = await engine.WaitForCompletionAsync<string>(
            runId,
            cancellationToken: TestContext.Current.CancellationToken);
        original.Should().Be("child:parent:go");
        var before = await engine.GetRunsAsync(
            new RunQuery { WorkflowName = "child" },
            cancellationToken: TestContext.Current.CancellationToken);
        before.Should().ContainSingle().Which.ParentRunId.Should().Be(runId);
        var firstChildId = before.Single().Id;

        await engine.RestartStepAsync(
            runId,
            "child:start",
            TestContext.Current.CancellationToken);
        await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
        var rerun = await engine.WaitForCompletionAsync<string>(
            runId,
            cancellationToken: TestContext.Current.CancellationToken);
        rerun.Should().Be("child:parent:go");
        var after = await engine.GetRunsAsync(
            new RunQuery { WorkflowName = "child" },
            cancellationToken: TestContext.Current.CancellationToken);
        after.Should().HaveCount(2);
        after.Should().Contain(r => r.Id == firstChildId);
        // A fresh child identity must be used after restart (new invocation generation).
        var newChild = after.Single(r => r.Id != firstChildId);
        newChild.ParentRunId.Should().Be(runId);
    }

    [Fact]
    public async Task StartChildAsync_RestartWithChangedInputRejectsExistingChildRun()
    {
        var firstEngine = CreateEngine(
            new WorkflowRegistry()
                .Register("mutable-parent", "1", new MutableChildInputParentWorkflow())
                .Register("child", "1", new ChildWorkflow()));
        var runId = await firstEngine.StartAsync(
            "mutable-parent",
            "1",
            "go",
            cancellationToken: TestContext.Current.CancellationToken);
        await firstEngine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
        await firstEngine.WaitForCompletionAsync<string>(
            runId,
            cancellationToken: TestContext.Current.CancellationToken);

        await firstEngine.RestartStepAsync(
            runId,
            "parent-step",
            TestContext.Current.CancellationToken);
        var changedEngine = CreateEngine(
            new WorkflowRegistry()
                .Register(
                    "mutable-parent",
                    "1",
                    new MutableChildInputParentWorkflow { Suffix = "b" })
                .Register("child", "1", new ChildWorkflow()));
        await changedEngine.ExecuteAsync(runId, TestContext.Current.CancellationToken);

        var action = () => changedEngine.WaitForCompletionAsync<string>(
            runId,
            cancellationToken: TestContext.Current.CancellationToken);
        await action.Should().ThrowAsync<WorkflowExecutionFailedException>()
            .WithMessage("*incompatible input or result contract*");
    }

    [Fact]
    public async Task StartChildAsync_RecordsWaitToStartDependencyEdge()
    {
        var parent = new ParentWorkflow();
        var child = new ChildWorkflow();
        var engine = CreateEngine(
            new WorkflowRegistry()
                .Register("parent-edge", "1", parent)
                .Register("child", "1", child));
        var runId = await engine.StartAsync(
            "parent-edge",
            "1",
            "go",
            cancellationToken: TestContext.Current.CancellationToken);
        await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
        await engine.WaitForCompletionAsync<string>(
            runId,
            cancellationToken: TestContext.Current.CancellationToken);

        var graph = await engine.GetDependencyGraphAsync(
            runId,
            TestContext.Current.CancellationToken);
        graph.Should().Contain(item =>
            item.StepKey == "child:wait" && item.DependsOnStepKey == "child:start");

        var plan = await engine.RestartStepAsync(
            runId,
            "child:start",
            cancellationToken: TestContext.Current.CancellationToken);
        plan.StepsToInvalidate.Select(item => item.StepKey)
            .Should().Equal("child:start", "child:wait");

        await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
        var rerun = await engine.WaitForCompletionAsync<string>(
            runId,
            cancellationToken: TestContext.Current.CancellationToken);
        rerun.Should().Be("child:parent:go");
        (await engine.GetRunsAsync(
            new RunQuery { WorkflowName = "child" },
            cancellationToken: TestContext.Current.CancellationToken))
            .Should().HaveCount(2);
    }

    [Fact]
    public async Task ChildRun_PinsDefinitionFingerprint()
    {
        var (registry, compiled) = ChainDefinition("a", "b");
        registry.Register("parent", "1", new FingerprintParentWorkflow());
        await using var engine = CreateEngine(registry);
        var id = await engine.StartAsync(
            "parent",
            "1",
            JsonSerializer.SerializeToElement("x"),
            cancellationToken: TestContext.Current.CancellationToken);
        await engine.ExecuteAsync(id, TestContext.Current.CancellationToken);
        var child = (await engine.GetRunsAsync(
            new RunQuery(),
            cancellationToken: TestContext.Current.CancellationToken))
            .Single(r => r.ParentRunId == id);
        child.DefinitionFingerprint.Should().Be(compiled.Fingerprint);
    }

    private static (WorkflowRegistry, CompiledWorkflowDefinition) ChainDefinition(
        string first, string last)
    {
        var catalogue = new ActivityCatalogue();
        catalogue.Register(new ActivityReference("append", "1"), new AppendActivity());
        var compiled = WorkflowCompiler.Compile(new DeclarativeWorkflowDefinition
        {
            Name = "chain",
            Version = "1",
            Steps = [
                new() { Id = first, Activity = new("append", "1") },
                new() { Id = last, Activity = new("append", "1"), DependsOn = [first] }]
        }, catalogue).Compiled!;
        return (new WorkflowRegistry().RegisterDeclarative(compiled, catalogue), compiled);
    }

    private sealed class AppendActivity : IActivity<string, string>
    {
        public Task<string> ExecuteAsync(string input, CancellationToken cancellationToken) =>
            Task.FromResult(input + "!");
    }

    private sealed class FingerprintParentWorkflow : IWorkflow<JsonElement, JsonElement>
    {
        public Task<JsonElement> RunAsync(
            WorkflowContext context, JsonElement input, CancellationToken cancellationToken) =>
            context.StartChildAsync<JsonElement, JsonElement>(
                "child", "chain", "1", input, cancellationToken);
    }

    [Fact]
    public async Task ChildRun_RejectsChangedDefinitionBeforeNewActivity()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore();
        var childId = await StartWaitingParentAsync(store, ct);

        await using (var swapped = new WorkflowEngine(
            store,
            new WorkflowRegistry()
                .Register("parent", "1", new SignalChildParentWorkflow())
                .Register("waiting-child", "1", new WaitingChildWorkflow("child-v2"))))
        {
            await swapped.ExecuteAsync(childId, ct);
        }

        var child = (await store.GetRunAsync(childId, ct))!;
        child.Status.Should().Be(WorkflowStatus.Failed);
        child.Error.Should().NotBeNull();
        child.Error!.Message.Should().Contain("fingerprint");
    }

    private static async Task<Guid> StartWaitingParentAsync(SqliteWorkflowStore store, CancellationToken ct)
    {
        var engine = new WorkflowEngine(
            store,
            new WorkflowRegistry()
                .Register("parent", "1", new SignalChildParentWorkflow())
                .Register("waiting-child", "1", new WaitingChildWorkflow("child-v1")));
        var id = await engine.StartAsync("parent", "1", "x", cancellationToken: ct);
        var execution = engine.ExecuteAsync(id, ct);
        try
        {
            return await WaitForChildAsync(store, id, ct);
        }
        finally
        {
            await engine.DisposeAsync();
            try { await execution; } catch (OperationCanceledException) { }
        }
    }

    private static async Task<Guid> WaitForChildAsync(
        SqliteWorkflowStore store, Guid parentId, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var children = await store.GetRunsAsync(new RunQuery(), ct);
            var child = children.SingleOrDefault(r => r.ParentRunId == parentId);
            if (child is not null)
                return child.Id;
            await Task.Delay(10, ct);
        }
        throw new TimeoutException("Child run was not admitted.");
    }

    private sealed class SignalChildParentWorkflow : IWorkflow<string, string>
    {
        public Task<string> RunAsync(
            WorkflowContext context, string input, CancellationToken cancellationToken) =>
            context.StartChildAsync<string, string>(
                "kid", "waiting-child", "1", input, cancellationToken);
    }

    private sealed class WaitingChildWorkflow(string fingerprint) : IWorkflow<string, string>, IWorkflowFingerprint
    {
        public string Fingerprint => fingerprint;

        public Task<string> RunAsync(
            WorkflowContext context, string input, CancellationToken cancellationToken) =>
            context.WaitForSignalAsync<string>("hold", "go", cancellationToken: cancellationToken);
    }
}
