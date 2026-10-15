using System.Reflection;
using System.Text.Json;
using FluentAssertions;

namespace Penghou.Zhinu.Tests;

public sealed class ActivityGroupAdmissionTests
{
    [Fact]
    public async Task GroupedStart_RequiresStoreCapabilityBeforeRunCreationOrWorkflowFactory()
    {
        var store = DispatchProxy.Create<IWorkflowStore, NoGroupStoreProxy>();
        var storeProbe = (NoGroupStoreProxy)(object)store;
        var manifest = Manifest("group");
        var factoryCalls = 0;
        var callbackCalls = 0;
        var registry = new WorkflowRegistry().Register(
            new WorkflowRegistration<string, string>(
                new WorkflowDefinition { Name = "grouped", Version = "1" },
                () =>
                {
                    factoryCalls++;
                    return new CallbackWorkflow(() => callbackCalls++);
                },
                manifest));
        await using var engine = new WorkflowEngine(store, registry);

        var start = () => engine.StartAsync("grouped", "1", "input");

        await start.Should().ThrowAsync<WorkflowConfigurationException>();
        storeProbe.CreateRunCalls.Should().Be(0);
        factoryCalls.Should().Be(0);
        callbackCalls.Should().Be(0);
    }

    [Fact]
    public async Task ValidateRun_RejectsMissingPersistedManifest()
    {
        var manifest = Manifest("group");
        var store = DispatchProxy.Create<IGroupTestStore, GroupStoreProxy>();
        ((GroupStoreProxy)(object)store).PersistedManifest = null;
        var run = GroupedRun(manifest);

        var validate = () => ActivityGroupRuntime.ValidateRunAsync(
            store,
            run,
            manifest,
            CancellationToken.None).AsTask();

        await validate.Should().ThrowAsync<WorkflowConfigurationException>();
    }

    [Fact]
    public async Task ValidateRun_RejectsChangedPersistedManifest()
    {
        var admitted = Manifest("group", displayName: "Original");
        var changed = Manifest("group", displayName: "Changed");
        var store = DispatchProxy.Create<IGroupTestStore, GroupStoreProxy>();
        ((GroupStoreProxy)(object)store).PersistedManifest = changed;
        var run = GroupedRun(admitted);

        var validate = () => ActivityGroupRuntime.ValidateRunAsync(
            store,
            run,
            admitted,
            CancellationToken.None).AsTask();

        await validate.Should().ThrowAsync<WorkflowConfigurationException>();
    }

    [Fact]
    public void LegacyCustomRegistration_RemainsUngroupedWithoutStoreCapability()
    {
        var registration = new LegacyRegistration();
        var store = DispatchProxy.Create<IWorkflowStore, NoGroupStoreProxy>();

        var captured = ActivityGroupRuntime.Capture(registration, store);

        captured.Should().BeNull();
    }

    [Fact]
    public void WorkflowRegistration_ExposesExplicitManifestProviderCapability()
    {
        var manifest = Manifest("group");
        var registration = new WorkflowRegistration<string, string>(
            new WorkflowDefinition { Name = "grouped", Version = "1" },
            () => new CallbackWorkflow(() => { }),
            manifest);

        registration.Should().BeAssignableTo<IWorkflowGroupManifestProvider>();
        ((IWorkflowGroupManifestProvider)registration).GroupManifest.Should().BeSameAs(manifest);
    }

    private static ActivityGroupManifest Manifest(string key, string displayName = "Group") =>
        ActivityGroupManifest.Create(new[]
        {
            new ActivityGroupDefinition(key, ActivityGroupKind.Logical, displayName)
        });

    private static WorkflowRun GroupedRun(ActivityGroupManifest manifest) =>
        new()
        {
            Id = Guid.Parse("ac09fa13-1206-402f-8657-fb318bf3926e"),
            WorkflowName = "grouped",
            WorkflowVersion = "1",
            Status = WorkflowStatus.Pending,
            CreatedAt = DateTimeOffset.UnixEpoch,
            UpdatedAt = DateTimeOffset.UnixEpoch,
            GroupManifestDigest = manifest.ManifestDigest
        };

    public interface IGroupTestStore : IWorkflowStore, IWorkflowGroupRepository { }

    public class NoGroupStoreProxy : DispatchProxy
    {
        public int CreateRunCalls { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            switch (targetMethod.Name)
            {
                case nameof(IWorkflowRepository.InitializeAsync):
                    return ValueTask.CompletedTask;
                case nameof(IWorkflowLeaseRepository.RecoverExpiredLeasesAsync):
                    return new ValueTask<int>(0);
                case nameof(IWorkflowRepository.CreateRunAsync):
                    CreateRunCalls++;
                    return ValueTask.CompletedTask;
                default:
                    throw new NotSupportedException($"Unexpected store call '{targetMethod.Name}'.");
            }
        }
    }

    public class GroupStoreProxy : DispatchProxy
    {
        public ActivityGroupManifest? PersistedManifest { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            if (targetMethod.Name == nameof(IWorkflowGroupRepository.GetGroupManifestAsync))
                return new ValueTask<ActivityGroupManifest?>(PersistedManifest);
            if (targetMethod.Name == nameof(IWorkflowRepository.InitializeAsync))
                return ValueTask.CompletedTask;
            if (targetMethod.Name == nameof(IWorkflowLeaseRepository.RecoverExpiredLeasesAsync))
                return new ValueTask<int>(0);
            throw new NotSupportedException($"Unexpected grouped store call '{targetMethod.Name}'.");
        }
    }

    private sealed class CallbackWorkflow(Action callback) : IWorkflow<string, string>
    {
        public Task<string> RunAsync(
            WorkflowContext context,
            string input,
            CancellationToken cancellationToken)
        {
            callback();
            return Task.FromResult(input);
        }
    }

    private sealed class LegacyRegistration : IWorkflowRegistration
    {
        public WorkflowDefinition Definition { get; } = new() { Name = "legacy", Version = "1" };
        public Type InputType => typeof(string);
        public Type OutputType => typeof(string);
        public string? DefinitionFingerprint => null;
        public string SerializeInput(object? input, JsonSerializerOptions options) =>
            JsonSerializer.Serialize(input, options);
        public Task<string?> ExecuteAsync(
            WorkflowContext context,
            string inputJson,
            JsonSerializerOptions options,
            CancellationToken cancellationToken) => Task.FromResult<string?>(inputJson);
    }
}
