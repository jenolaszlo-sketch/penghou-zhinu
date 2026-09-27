using FluentAssertions;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;

namespace Penghou.Zhinu.Sqlite.Tests;

/// <summary>
/// Engine run-to-generation binding: every started or forked run opens its
/// own instance with an active first generation, idempotent restarts neither
/// duplicate nor drop the binding, and restarts preserve it.
/// </summary>
public sealed class RunGenerationBindingTests : WorkflowEngineTestBase
{
    [Fact]
    public async Task Start_BindsActiveFirstGeneration()
    {
        var ct = TestContext.Current.CancellationToken;
        var engine = CreateEngine(new SignalWorkflow(), "run-binding");
        var store = PeerStore();
        var runId = await engine.StartAsync("run-binding", "1", "x", cancellationToken: ct);

        var generation = await store.GetGenerationByRunAsync(runId, ct);

        generation.Should().NotBeNull();
        generation!.Status.Should().Be(WorkflowGenerationStatus.Active);
        generation.Ordinal.Should().Be(1);
        generation.PredecessorGenerationId.Should().BeNull();
        var run = (await store.GetRunAsync(runId, ct))!;
        generation.PlanRevision.Should().Be(run.DefinitionFingerprint);
        var instance = (await store.GetInstanceAsync(generation.InstanceId, ct))!;
        instance.MetadataJson.Should().Contain("run-binding");
        (await store.GetActiveGenerationAsync(instance.InstanceId, ct))!
            .GenerationId.Should().Be(generation.GenerationId);
    }

    [Fact]
    public async Task IdempotentStart_DoesNotDuplicateBinding()
    {
        var ct = TestContext.Current.CancellationToken;
        var engine = CreateEngine(new SignalWorkflow(), "run-binding-idempotent");
        var store = PeerStore();
        var runId = Guid.NewGuid();

        var first = await engine.StartAsync(
            "run-binding-idempotent", "1", "x", runId, cancellationToken: ct);
        var second = await engine.StartAsync(
            "run-binding-idempotent", "1", "x", runId, cancellationToken: ct);

        second.Should().Be(first);
        var generation = (await store.GetGenerationByRunAsync(runId, ct))!;
        (await store.ListGenerationsAsync(generation.InstanceId, ct))
            .Should().ContainSingle();
    }

    [Fact]
    public async Task IdempotentStart_HealsUnboundRun()
    {
        var ct = TestContext.Current.CancellationToken;
        var engine = CreateEngine(new SignalWorkflow(), "run-binding-heal");
        var store = PeerStore();
        var template = await engine.StartAsync(
            "run-binding-heal", "1", "x", cancellationToken: ct);
        var source = (await store.GetRunAsync(template, ct))!;
        var runId = Guid.NewGuid();
        await store.CreateRunAsync(
            source with { Id = runId },
            ct);
        (await store.GetGenerationByRunAsync(runId, ct)).Should().BeNull();

        var returned = await engine.StartAsync(
            "run-binding-heal", "1", "x", runId, cancellationToken: ct);

        returned.Should().Be(runId);
        (await store.GetGenerationByRunAsync(runId, ct))!
            .Status.Should().Be(WorkflowGenerationStatus.Active);
    }

    [Fact]
    public async Task Fork_BindsSeparateInstance()
    {
        var ct = TestContext.Current.CancellationToken;
        var workflow = new DependentStepsWorkflow();
        var engine = CreateEngine(workflow, "run-binding-fork");
        var store = PeerStore();
        await engine.RunAsync<string, string>(
            "run-binding-fork", "1", "x", cancellationToken: ct);
        var sourceBinding = (await store.GetGenerationByRunAsync(workflow.RunId, ct))!;

        var forkId = await engine.ForkAsync(workflow.RunId, "b", cancellationToken: ct);

        var forkBinding = (await store.GetGenerationByRunAsync(forkId, ct))!;
        forkBinding.Status.Should().Be(WorkflowGenerationStatus.Active);
        forkBinding.InstanceId.Should().NotBe(sourceBinding.InstanceId);
        (await store.GetGenerationByRunAsync(workflow.RunId, ct))!
            .GenerationId.Should().Be(sourceBinding.GenerationId);
    }

    [Fact]
    public async Task Restart_PreservesBinding()
    {
        var ct = TestContext.Current.CancellationToken;
        var engine = CreateEngine(new TwoStepWorkflow(), "run-binding-restart");
        var store = PeerStore();
        var runId = await engine.StartAsync(
            "run-binding-restart", "1", "x", cancellationToken: ct);
        await engine.ExecuteAsync(runId, ct);
        var before = (await store.GetGenerationByRunAsync(runId, ct))!;

        await engine.RestartStepAsync(runId, "first", ct);

        var after = (await store.GetGenerationByRunAsync(runId, ct))!;
        after.GenerationId.Should().Be(before.GenerationId);
        after.Status.Should().Be(WorkflowGenerationStatus.Active);
    }

    private SqliteWorkflowStore PeerStore() =>
        new(new ZhinuSqliteOptions
        {
            DatabasePath = Path.Combine(root, "zhinu.db"),
            BusyTimeout = TimeSpan.FromSeconds(2),
            Pooling = false
        });
}
