using System.Reflection;
using System.Runtime.ExceptionServices;
using FluentAssertions;

namespace Penghou.Zhinu.Sqlite.Tests;

public sealed class ActivityGroupBoundaryTests : WorkflowEngineTestBase
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ChangedOrMissingRegistration_RejectsRestartAndRollbackBeforeAnyWrite()
    {
        var store = CreateStore();
        var manifest = Manifest("Original");
        var runId = await CompleteGroupedRunAsync(store, manifest, "boundary-run");
        var changed = Manifest("Changed");

        var cases = new (WorkflowRegistry Registry, Type ExceptionType)[]
        {
            (Registry(changed, "boundary-run"), typeof(WorkflowConfigurationException)),
            (RegistryWithoutManifest("boundary-run"), typeof(WorkflowConfigurationException)),
            (new WorkflowRegistry(), typeof(WorkflowDefinitionUnavailableException))
        };
        foreach (var (registry, exceptionType) in cases)
        {
            await using var engine = new WorkflowEngine(store, registry);
            foreach (var operation in new Func<Task>[]
            {
                () => engine.RestartStepAsync(runId, "work", cancellationToken: Ct),
                () => engine.RestartStepWithReceiptAsync(runId, "work",
                    new RestartStepOptions { OperationId = Guid.NewGuid() }, Ct),
                () => engine.RollbackAsync(runId, cancellationToken: Ct),
                () => engine.RollbackToStepAsync(runId, "work", RollbackBoundary.BeforeStep,
                    cancellationToken: Ct),
                () => engine.RollbackAndRestartAsync(runId, cancellationToken: Ct)
            })
            {
                var before = await SnapshotAsync(store, runId);
                var act = operation;
                var thrown = await act.Should().ThrowAsync<ZhinuException>();
                thrown.Which.GetType().Should().Be(exceptionType);
                (await SnapshotAsync(store, runId)).Should().BeEquivalentTo(before);
            }
        }
    }

    [Fact]
    public async Task Execute_RejectsGroupedTerminalRunWhenStoreCapabilityIsHidden()
    {
        var backing = CreateStore();
        var manifest = Manifest("Run");
        var runId = await CompleteGroupedRunAsync(backing, manifest, "terminal-run");
        var before = await SnapshotAsync(backing, runId);
        var hidden = HideGroupCapability(backing);
        await using var engine = new WorkflowEngine(hidden, Registry(manifest, "terminal-run"));

        var act = () => engine.ExecuteAsync(runId, Ct);
        await act.Should().ThrowAsync<WorkflowConfigurationException>();
        (await SnapshotAsync(backing, runId)).Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task GroupedFork_MissingOptionalCapabilityRejectsBeforeDestinationIsCreated()
    {
        var store = CreateStore();
        var manifest = Manifest("Fork");
        var runId = await CompleteGroupedRunAsync(store, manifest, "fork-run");
        var hidden = DispatchProxy.Create<IGroupFoundationStore, GroupCapabilityHidingProxy>();
        ((GroupCapabilityHidingProxy)(object)hidden).Target = store;
        await using var engine = new WorkflowEngine(hidden, Registry(manifest, "fork-run"));
        var beforeRuns = await engine.GetRunsAsync(new RunQuery(), Ct);
        var destinationId = Guid.NewGuid();

        var act = () => engine.ForkAsync(runId, "work", new ForkRunOptions { WorkflowRunId = destinationId }, Ct);
        await act.Should().ThrowAsync<WorkflowConfigurationException>();
        (await engine.GetRunAsync(destinationId, Ct)).Should().BeNull();
        (await engine.GetRunsAsync(new RunQuery(), Ct)).Should().BeEquivalentTo(beforeRuns);
    }

    [Fact]
    public async Task ChildRun_UsesItsOwnManifestAndMissingCapabilityFailsBeforeChildCreation()
    {
        var childManifest = Manifest("Child");
        var childCalls = 0;
        var parent = new ChildStarterWorkflow();
        var child = new ChildWorkflow(() => Interlocked.Increment(ref childCalls));
        var registry = new WorkflowRegistry()
            .Register("group-parent", "1", parent)
            .Register(new WorkflowRegistration<string, string>(
                new WorkflowDefinition { Name = "group-child", Version = "1" },
                () => child,
                childManifest));

        var store = CreateStore();
        await using (var engine = new WorkflowEngine(store, registry))
        {
            var parentId = await engine.StartAsync("group-parent", "1", "input", cancellationToken: Ct);
            await engine.ExecuteAsync(parentId, Ct);
            (await engine.WaitForCompletionAsync<string>(parentId, cancellationToken: Ct)).Should().Be("child");
            childCalls.Should().Be(1);
            var childRun = (await engine.GetRunsAsync(new RunQuery { WorkflowName = "group-child" }, Ct)).Single();
            childRun.GroupManifestDigest.Should().Be(childManifest.ManifestDigest);
            (await store.GetGroupManifestAsync(childRun.Id, Ct))!.CanonicalJson.Should().Be(childManifest.CanonicalJson);
            (await engine.GetRunAsync(parentId, Ct))!.GroupManifestDigest.Should().BeNull();
        }

        childCalls = 0;
        var hiddenBacking = CreateStore();
        var proxy = HideGroupCapability(hiddenBacking);
        await using var hiddenEngine = new WorkflowEngine(proxy, registry);
        var hiddenParentId = await hiddenEngine.StartAsync("group-parent", "1", "input", cancellationToken: Ct);
        await hiddenEngine.ExecuteAsync(hiddenParentId, Ct);
        var completion = () => hiddenEngine.WaitForCompletionAsync<string>(hiddenParentId, cancellationToken: Ct);
        await completion.Should().ThrowAsync<WorkflowExecutionFailedException>();
        (await hiddenEngine.GetRunAsync(hiddenParentId, Ct))!.Status.Should().Be(WorkflowStatus.Failed);
        childCalls.Should().Be(0);
        (await hiddenEngine.GetRunsAsync(new RunQuery { WorkflowName = "group-child" }, Ct))
            .Should().NotContain(run => run.ParentRunId == hiddenParentId);
    }

    [Fact]
    public async Task StartHandleAndRun_RequireGroupedStoreCapabilityBeforeCreatingRun()
    {
        var backing = CreateStore();
        var manifest = Manifest("Entry");
        var workflow = new GroupedWorkflow();
        await using var engine = new WorkflowEngine(HideGroupCapability(backing), Registry(manifest, "entry-run", workflow));
        var id = Guid.NewGuid();

        var start = () => engine.StartHandleAsync<string, string>(
            "entry-run", "1", "input", id, null, null, cancellationToken: Ct);
        await start.Should().ThrowAsync<WorkflowConfigurationException>();
        (await backing.GetRunAsync(id, Ct)).Should().BeNull();

        var runId = Guid.NewGuid();
        var run = () => engine.RunAsync<string, string>(
            "entry-run", "1", "input", runId, null, null, cancellationToken: Ct);
        await run.Should().ThrowAsync<WorkflowConfigurationException>();
        (await backing.GetRunAsync(runId, Ct)).Should().BeNull();
    }

    [Fact]
    public async Task CustomRegistryGroupProvider_IsCapturedBeforeInputSerializationAndAtomicStart()
    {
        var manifest = Manifest("Custom registration");
        var hiddenBacking = CreateStore();
        var hiddenRegistration = new CountingGroupRegistration(manifest);
        var hiddenEngine = new WorkflowEngine(
            HideGroupCapability(hiddenBacking), new SingleRegistrationRegistry(hiddenRegistration));
        var rejectedId = Guid.NewGuid();

        var rejected = () => hiddenEngine.StartAsync("custom-provider", "1", "input", rejectedId, cancellationToken: Ct);
        await rejected.Should().ThrowAsync<WorkflowConfigurationException>();
        hiddenRegistration.SerializeCalls.Should().Be(0);
        hiddenRegistration.ExecuteCalls.Should().Be(0);
        (await hiddenBacking.GetRunAsync(rejectedId, Ct)).Should().BeNull();

        var realStore = CreateStore();
        var acceptedRegistration = new CountingGroupRegistration(manifest);
        await using var engine = new WorkflowEngine(realStore, new SingleRegistrationRegistry(acceptedRegistration));
        var acceptedId = await engine.StartAsync("custom-provider", "1", "input", cancellationToken: Ct);
        acceptedRegistration.SerializeCalls.Should().Be(1);
        acceptedRegistration.ExecuteCalls.Should().Be(0);
        (await realStore.GetRunAsync(acceptedId, Ct))!.GroupManifestDigest.Should().Be(manifest.ManifestDigest);
        (await realStore.GetGroupManifestAsync(acceptedId, Ct))!.CanonicalJson.Should().Be(manifest.CanonicalJson);
    }

    [Theory]
    [InlineData(FanOutVariant.FunctionalIndex)]
    [InlineData(FanOutVariant.FunctionalKeyed)]
    [InlineData(FanOutVariant.ClassIndex)]
    [InlineData(FanOutVariant.ClassKeyed)]
    public async Task GroupedFanOutOverloads_RejectBeforeAnyItemCallback(FanOutVariant variant)
    {
        var store = CreateStore();
        var callbacks = 0;
        var resolver = new CountingStepResolver(() => Interlocked.Increment(ref callbacks));
        var workflow = new FanOutWorkflow(variant, () => Interlocked.Increment(ref callbacks));
        var manifest = Manifest("Fan out");
        var name = $"group-fanout-{variant}";
        var registration = new WorkflowRegistration<string, string>(
            new WorkflowDefinition { Name = name, Version = "1" }, () => workflow, manifest);
        await using var engine = new WorkflowEngine(store, new WorkflowRegistry().Register(registration),
            new ZhinuOptions(), null, null, null, null, resolver);
        var id = await engine.StartAsync(name, "1", "input", cancellationToken: Ct);

        await engine.ExecuteAsync(id, Ct);
        var completion = () => engine.WaitForCompletionAsync<string>(id, cancellationToken: Ct);
        await completion.Should().ThrowAsync<WorkflowExecutionFailedException>();
        callbacks.Should().Be(0);
        resolver.ResolveCalls.Should().Be(0);
        (await engine.GetStepsAsync(id, Ct)).Should().BeEmpty();
    }

    private async Task<Guid> CompleteGroupedRunAsync(
        SqliteWorkflowStore store,
        ActivityGroupManifest manifest,
        string name)
    {
        await using var engine = new WorkflowEngine(store, Registry(manifest, name));
        var id = await engine.StartAsync(name, "1", "input", cancellationToken: Ct);
        await engine.ExecuteAsync(id, Ct);
        (await engine.WaitForCompletionAsync<string>(id, cancellationToken: Ct)).Should().Be("done");
        return id;
    }

    private static WorkflowRegistry Registry(
        ActivityGroupManifest manifest,
        string name,
        GroupedWorkflow? workflow = null) => new WorkflowRegistry().Register(
        new WorkflowRegistration<string, string>(
            new WorkflowDefinition { Name = name, Version = "1" },
            () => workflow ?? new GroupedWorkflow(),
            manifest));

    private static WorkflowRegistry RegistryWithoutManifest(string name) =>
        new WorkflowRegistry().Register(name, "1", new GroupedWorkflow());

    private static ActivityGroupManifest Manifest(string displayName) => ActivityGroupManifest.Create(
    [new ActivityGroupDefinition("unit", ActivityGroupKind.Logical, displayName)]);

    private static async Task<DurableSnapshot> SnapshotAsync(SqliteWorkflowStore store, Guid runId)
    {
        var run = await store.GetRunAsync(runId, Ct);
        var steps = await store.GetStepsAsync(runId, Ct);
        var events = await store.GetEventsAsync(runId, afterSequence: 0, limit: 100, cancellationToken: Ct);
        return new DurableSnapshot(
            run,
            steps.OrderBy(step => step.StepKey, StringComparer.Ordinal)
                .Select(step => (step.Id, step.StepKey, step.Revision, step.Status, step.Attempt))
                .ToArray(),
            events.Select(item => (item.Sequence, item.EventType, item.StepKey, item.DataJson)).ToArray());
    }

    private static IWorkflowStore HideGroupCapability(IWorkflowStore store)
    {
        var proxy = DispatchProxy.Create<IWorkflowStore, GroupCapabilityHidingProxy>();
        ((GroupCapabilityHidingProxy)(object)proxy).Target = store;
        return proxy;
    }

    public interface IGroupFoundationStore : IWorkflowStore, IWorkflowGroupRepository { }

    private sealed record DurableSnapshot(
        WorkflowRun? Run,
        (Guid Id, string StepKey, int Revision, StepStatus Status, int Attempt)[] Steps,
        (long Sequence, string EventType, string? StepKey, string? DataJson)[] Events);

    public class GroupCapabilityHidingProxy : DispatchProxy
    {
        public IWorkflowStore Target { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            try
            {
                return targetMethod!.Invoke(Target, args);
            }
            catch (TargetInvocationException exception) when (exception.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
                throw;
            }
        }
    }

    private sealed class SingleRegistrationRegistry(IWorkflowRegistration registration) : IWorkflowRegistry
    {
        public IWorkflowRegistration Get(string name, string version) =>
            TryGet(name, version, out var value)
                ? value!
                : throw new WorkflowDefinitionUnavailableException(name, version);

        public bool TryGet(string name, string version, out IWorkflowRegistration? value)
        {
            value = registration.Definition.Name == name && registration.Definition.Version == version
                ? registration
                : null;
            return value is not null;
        }
    }

    private sealed class CountingGroupRegistration(ActivityGroupManifest manifest)
        : IWorkflowRegistration, IWorkflowGroupManifestProvider
    {
        private readonly WorkflowRegistration<string, string> inner = new(
            new WorkflowDefinition { Name = "custom-provider", Version = "1" },
            () => new GroupedWorkflow(), manifest);

        public ActivityGroupManifest? GroupManifest => manifest;
        public WorkflowDefinition Definition => inner.Definition;
        public Type InputType => inner.InputType;
        public Type OutputType => inner.OutputType;
        public string? DefinitionFingerprint => inner.DefinitionFingerprint;
        public int SerializeCalls { get; private set; }
        public int ExecuteCalls { get; private set; }

        public string SerializeInput(object? input, System.Text.Json.JsonSerializerOptions options)
        {
            SerializeCalls++;
            return inner.SerializeInput(input, options);
        }

        public Task<string?> ExecuteAsync(WorkflowContext context, string inputJson,
            System.Text.Json.JsonSerializerOptions options, CancellationToken cancellationToken)
        {
            ExecuteCalls++;
            return inner.ExecuteAsync(context, inputJson, options, cancellationToken);
        }
    }

    public enum FanOutVariant { FunctionalIndex, FunctionalKeyed, ClassIndex, ClassKeyed }

    private sealed class FanOutWorkflow(FanOutVariant variant, Action callback) : IWorkflow<string, string>
    {
        public async Task<string> RunAsync(WorkflowContext context, string input, CancellationToken cancellationToken)
        {
            var occurrence = context.GetActivityGroup("unit");
            var options = new StepOptions
            {
                Group = new ActivityGroupStepBinding(new ActivityGroupMembership("unit"), occurrence)
            };
            var items = new[] { "item" };
            switch (variant)
            {
                case FanOutVariant.FunctionalIndex:
                    return (await context.FanOutAsync("fan", items,
                        (value, _, _) => { callback(); return Task.FromResult(value); }, options, cancellationToken))[0];
                case FanOutVariant.FunctionalKeyed:
                    return (await context.FanOutAsync("fan", items, value => value,
                        (value, _, _) => { callback(); return Task.FromResult(value); },
                        new FanOutOptions { MaxDegreeOfParallelism = 1 }, options, cancellationToken))[0];
                case FanOutVariant.ClassIndex:
                    return (await context.FanOutAsync("fan",
                        new WorkflowStepReference<string, string>(new StepImplementationKey("fan-step")),
                        items, options, cancellationToken))[0];
                default:
                    return (await context.FanOutAsync("fan",
                        new WorkflowStepReference<string, string>(new StepImplementationKey("fan-step")),
                        items, value => value, new FanOutOptions { MaxDegreeOfParallelism = 1 },
                        options, cancellationToken))[0];
            }
        }
    }

    private sealed class CountingStepResolver(Action callback) : IWorkflowStepResolver
    {
        public int ResolveCalls { get; private set; }

        public ValueTask<IWorkflowStepLease<TStep>> ResolveAsync<TStep>(
            StepImplementationKey implementationKey, CancellationToken cancellationToken)
            where TStep : class
        {
            ResolveCalls++;
            var step = new CountingStep(callback);
            return ValueTask.FromResult<IWorkflowStepLease<TStep>>(
                new StepLease<TStep>((TStep)(object)step));
        }
    }

    private sealed class StepLease<TStep>(TStep step) : IWorkflowStepLease<TStep> where TStep : class
    {
        public TStep Step => step;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CountingStep(Action callback) : IWorkflowStep<string, string>
    {
        public Task<string> ExecuteAsync(WorkflowStepContext context, string input, CancellationToken cancellationToken)
        {
            callback();
            return Task.FromResult(input);
        }
    }

    private sealed class GroupedWorkflow : IWorkflow<string, string>
    {
        public async Task<string> RunAsync(WorkflowContext context, string input, CancellationToken cancellationToken)
        {
            var occurrence = context.GetActivityGroup("unit");
            return await context.StepAsync("work", input, (_, _, _) => Task.FromResult("done"),
                new StepOptions { Group = new ActivityGroupStepBinding(new ActivityGroupMembership("unit"), occurrence) },
                cancellationToken);
        }
    }

    private sealed class ChildStarterWorkflow : IWorkflow<string, string>
    {
        public Task<string> RunAsync(WorkflowContext context, string input, CancellationToken cancellationToken) =>
            context.StartChildAsync<string, string>("child", "group-child", "1", input, cancellationToken);
    }

    private sealed class ChildWorkflow(Action callback) : IWorkflow<string, string>
    {
        public Task<string> RunAsync(WorkflowContext context, string input, CancellationToken cancellationToken)
        {
            callback();
            return Task.FromResult("child");
        }
    }
}
