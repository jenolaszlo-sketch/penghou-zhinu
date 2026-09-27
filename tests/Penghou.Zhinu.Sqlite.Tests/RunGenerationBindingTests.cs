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

    [Fact]
    public async Task StartRetry_HealsPreparedFirstGeneration()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = PeerStore();
        await store.InitializeAsync(ct);
        var registry = new WorkflowRegistry().Register("echo", "1", new EchoWorkflow());
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await store.CreateRunAsync(
            new WorkflowRun
            {
                Id = id,
                WorkflowName = "echo",
                WorkflowVersion = "1",
                Status = WorkflowStatus.Pending,
                CreatedAt = now,
                UpdatedAt = now,
                InputJson = "\"x\"",
                InputType = SerializationIdentity.TypeId(typeof(string)),
                OutputType = SerializationIdentity.TypeId(typeof(string))
            },
            ct);
        var instance = await store.CreateInstanceAsync(null, ct);
        var generation = await store.CreateGenerationAsync(
            instance.InstanceId, id, null, null, null, ct);
        await store.PrepareGenerationAsync(generation.GenerationId, ct);
        await using var engine = new WorkflowEngine(store, registry);

        await engine.StartAsync("echo", "1", "x", workflowRunId: id, cancellationToken: ct);

        (await store.GetGenerationByRunAsync(id, ct))!
            .Status.Should().Be(WorkflowGenerationStatus.Active);
    }

    private sealed class EchoWorkflow : IWorkflow<string, string>
    {
        public Task<string> RunAsync(
            WorkflowContext context, string input, CancellationToken cancellationToken) =>
            context.StepAsync("echo", _ => Task.FromResult(input), cancellationToken: cancellationToken);
    }

    [Fact]
    public async Task StartRetry_HealsCreatedFirstGeneration()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = PeerStore();
        await store.InitializeAsync(ct);
        var registry = new WorkflowRegistry().Register("echo", "1", new EchoWorkflow());
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await store.CreateRunAsync(
            new WorkflowRun
            {
                Id = id,
                WorkflowName = "echo",
                WorkflowVersion = "1",
                Status = WorkflowStatus.Pending,
                CreatedAt = now,
                UpdatedAt = now,
                InputJson = "\"x\"",
                InputType = SerializationIdentity.TypeId(typeof(string)),
                OutputType = SerializationIdentity.TypeId(typeof(string))
            },
            ct);
        var instance = await store.CreateInstanceAsync(null, ct);
        await store.CreateGenerationAsync(instance.InstanceId, id, null, null, null, ct);
        await using var engine = new WorkflowEngine(store, registry);

        await engine.StartAsync("echo", "1", "x", workflowRunId: id, cancellationToken: ct);

        (await store.GetGenerationByRunAsync(id, ct))!
            .Status.Should().Be(WorkflowGenerationStatus.Active);
    }

    [Fact]
    public async Task StartRetry_DoesNotPromoteReviewCandidate()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = PeerStore();
        await store.InitializeAsync(ct);
        var registry = new WorkflowRegistry().Register("echo", "1", new EchoWorkflow());
        await using var engine = new WorkflowEngine(store, registry);
        var id = await engine.StartAsync("echo", "1", "x", cancellationToken: ct);
        var first = (await store.GetGenerationByRunAsync(id, ct))!;
        var candidate = await store.CreateGenerationAsync(
            first.InstanceId, id, "plan-review", "fp-review", first.GenerationId, ct);
        await store.PrepareGenerationAsync(candidate.GenerationId, ct);

        var act = () => engine.StartAsync(
            "echo", "1", "x", workflowRunId: id, cancellationToken: ct);

        await act.Should().ThrowAsync<WorkflowStateException>();
        (await store.GetGenerationAsync(first.GenerationId, ct))!
            .Status.Should().Be(WorkflowGenerationStatus.Active);
        (await store.GetGenerationAsync(candidate.GenerationId, ct))!
            .Status.Should().Be(WorkflowGenerationStatus.Prepared);
    }

    [Fact]
    public async Task ConcurrentIdenticalStarts_ConvergeOnOneBinding()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = PeerStore();
        await store.InitializeAsync(ct);
        var registry = new WorkflowRegistry().Register("echo", "1", new EchoWorkflow());
        await using var engine = new WorkflowEngine(store, registry);
        var id = Guid.NewGuid();

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            engine.StartAsync("echo", "1", "x", workflowRunId: id, cancellationToken: ct)));

        results.Should().OnlyContain(returned => returned == id);
        var binding = (await store.GetGenerationByRunAsync(id, ct))!;
        binding.Status.Should().Be(WorkflowGenerationStatus.Active);
        (await store.ListGenerationsAsync(binding.InstanceId, ct))
            .Should().ContainSingle();
    }

    [Fact]
    public async Task StartRetry_PreservesTerminalRunStatus()
    {
        var ct = TestContext.Current.CancellationToken;
        var engine = CreateEngine(new EchoWorkflow(), "binding-terminal");
        var store = PeerStore();
        var runId = await engine.StartAsync("binding-terminal", "1", "x", cancellationToken: ct);
        await engine.ExecuteAsync(runId, ct);
        await engine.WaitForCompletionAsync<string>(runId, cancellationToken: ct);

        var again = await engine.StartAsync(
            "binding-terminal", "1", "x", workflowRunId: runId, cancellationToken: ct);

        again.Should().Be(runId);
        (await store.GetRunAsync(runId, ct))!.Status.Should().Be(WorkflowStatus.Completed);
        (await store.GetGenerationByRunAsync(runId, ct))!
            .Status.Should().Be(WorkflowGenerationStatus.Active);
    }

    private SqliteWorkflowStore PeerStore() =>
        new(new ZhinuSqliteOptions
        {
            DatabasePath = Path.Combine(root, "zhinu.db"),
            BusyTimeout = TimeSpan.FromSeconds(2),
            Pooling = false
        });
}
