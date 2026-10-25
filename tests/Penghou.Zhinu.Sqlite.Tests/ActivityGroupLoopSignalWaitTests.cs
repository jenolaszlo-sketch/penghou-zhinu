using FluentAssertions;

namespace Penghou.Zhinu.Sqlite.Tests;

public sealed class ActivityGroupLoopSignalWaitTests : WorkflowEngineTestBase
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task GroupedLoopSignalWaitKeepsMembershipDependenciesAndIdentityAcrossRecovery()
    {
        var store = CreateStore();
        var registry = Registry(invalidBinding: false);
        const string waitKey = "$loop/approval-loop/1/body/approval";
        Guid runId;
        WorkflowStepRun originalWait;
        ActivityGroupStepMembership originalMembership;

        await using (var engine = new WorkflowEngine(store, registry))
        {
            runId = await engine.StartAsync("grouped-loop-signal", "1", "input", cancellationToken: Ct);
            using var interruption = new CancellationTokenSource();
            var execution = engine.ExecuteAsync(runId, interruption.Token);
            await WaitUntilAsync(
                () => HasStepStatusAsync(engine, runId, waitKey, StepStatus.Waiting, Ct), Ct);

            originalWait = (await engine.GetStepsAsync(runId, Ct)).Single(step => step.StepKey == waitKey);
            originalMembership = (await store.GetStepGroupMembershipAsync(originalWait.Id, Ct))!;
            originalMembership.Should().NotBeNull();
            originalMembership.StepRevision.Should().Be(originalWait.Revision);

            var dependencies = await engine.GetDependencyGraphAsync(runId, Ct);
            dependencies.Should().Contain(new StepDependency(
                waitKey, "$loop/approval-loop/1/condition"));
            dependencies.Should().Contain(new StepDependency(
                waitKey, "$loop/approval-loop/1/body/prepare"));

            await interruption.CancelAsync();
            await execution;
        }

        await using (var reopened = new WorkflowEngine(CreateStore(), registry))
        {
            await reopened.SendSignalAsync(runId, "release", "accepted", Ct);
            await reopened.ExecuteAsync(runId, Ct);
            (await reopened.WaitForCompletionAsync<string>(runId, cancellationToken: Ct))
                .Should().Be("accepted");

            var completedDependencies = await reopened.GetDependencyGraphAsync(runId, Ct);
            completedDependencies.Should().Contain(new StepDependency(
                "$loop/approval-loop/1/commit", waitKey));
            completedDependencies.Should().Contain(new StepDependency(
                "$loop/approval-loop/1/commit", "$loop/approval-loop/1/body/prepare"));

            var recoveredWait = (await reopened.GetStepsAsync(runId, Ct))
                .Single(step => step.StepKey == originalWait.StepKey);
            recoveredWait.Id.Should().Be(originalWait.Id);
            recoveredWait.Revision.Should().Be(originalWait.Revision);
            recoveredWait.Status.Should().Be(StepStatus.Completed);
            (await CreateStore().GetStepGroupMembershipAsync(recoveredWait.Id, Ct))
                .Should().BeEquivalentTo(originalMembership);
        }
    }

    [Fact]
    public async Task IncompatibleIterationGroupIsRejectedBeforeWaitClaim()
    {
        var store = CreateStore();
        await using var engine = new WorkflowEngine(store, Registry(invalidBinding: true));
        var runId = await engine.StartAsync("grouped-loop-signal", "1", "input", cancellationToken: Ct);

        await engine.ExecuteAsync(runId, Ct);

        (await store.GetStepsAsync(runId, Ct)).Should()
            .NotContain(step => step.StepKey == "$loop/approval-loop/1/body/approval");
        (await store.GetRunAsync(runId, Ct))!.Status.Should().Be(WorkflowStatus.Failed);
    }

    [Fact]
    public async Task ControlGroupSeparatesLoopBoundariesFromIterationWorkCounts()
    {
        var store = CreateStore();
        await using var engine = new WorkflowEngine(store, Registry(invalidBinding: false, controlRole: ActivityGroupMemberRole.Control));
        var runId = await engine.StartAsync("grouped-loop-signal", "1", "input", cancellationToken: Ct);
        await engine.SendSignalAsync(runId, "release", "accepted", Ct);
        await engine.ExecuteAsync(runId, Ct);
        (await engine.WaitForCompletionAsync<string>(runId, cancellationToken: Ct)).Should().Be("accepted");

        var steps = await engine.GetStepsAsync(runId, Ct);
        var controlSteps = new[]
        {
            "$loop/approval-loop/1/condition",
            "$loop/approval-loop/1/commit",
            "approval-loop"
        };
        var memberships = new List<ActivityGroupStepMembership>();
        foreach (var stepKey in controlSteps)
        {
            var step = steps.Single(item => item.StepKey == stepKey);
            var membership = (await store.GetStepGroupMembershipAsync(step.Id, Ct))!;
            membership.Role.Should().Be(ActivityGroupMemberRole.Control);
            memberships.Add(membership);
        }
        memberships.Select(item => item.GroupOccurrenceId).Distinct().Should().ContainSingle();
        var rootOccurrenceId = memberships.Select(item => item.GroupOccurrenceId).Distinct().Single();
        var waitMembership = (await store.GetStepGroupMembershipAsync(
            steps.Single(item => item.StepKey == "$loop/approval-loop/1/body/approval").Id, Ct))!;
        waitMembership.Role.Should().Be(ActivityGroupMemberRole.Work);
        waitMembership.GroupOccurrenceId.Should().NotBe(rootOccurrenceId);

        using var reader = new WorkflowGroupReader(CreateStore());
        var snapshot = (await reader.GetRunGroupSnapshotAsync(runId, Ct)).Value!;
        var root = (await reader.QueryGroupChildrenAsync(runId, snapshot.SnapshotToken, cancellationToken: Ct))
            .Value!.Items.Single(node => node.Occurrence?.OccurrenceId == rootOccurrenceId).Occurrence!;
        var child = (await reader.QueryGroupChildrenAsync(runId, snapshot.SnapshotToken,
            parentOccurrenceId: root.OccurrenceId, cancellationToken: Ct)).Value!
            .Items.Single(node => node.Occurrence?.GroupKey == "work").Occurrence!;
        child.ParentOccurrenceId.Should().Be(rootOccurrenceId);
        var state = (await reader.GetGroupStateAsync(runId, snapshot.SnapshotToken, root.OccurrenceId, Ct)).Value!;
        state.State.Work.Known.Should().Be(1);
        state.State.Control.Known.Should().Be(3);
    }

    [Fact]
    public async Task NonControlLoopBindingRejectsBeforeCreatingLoopSteps()
    {
        var store = CreateStore();
        await using var engine = new WorkflowEngine(store, Registry(invalidBinding: false, controlRole: ActivityGroupMemberRole.Work));
        var runId = await engine.StartAsync("grouped-loop-signal", "1", "input", cancellationToken: Ct);

        await engine.ExecuteAsync(runId, Ct);

        (await store.GetStepsAsync(runId, Ct)).Should().BeEmpty();
        (await store.GetRunAsync(runId, Ct))!.Status.Should().Be(WorkflowStatus.Failed);
    }

    [Fact]
    public async Task NativeIterationWaitReclaimsLegacyWrappedWaitWithSameSerializedInputIdentity()
    {
        var store = CreateStore();
        var legacyRegistry = Registry(invalidBinding: false, legacyWrapper: true);
        var waitKey = "$loop/approval-loop/1/body/approval";
        Guid runId;
        WorkflowStepRun legacyWait;

        await using (var legacyEngine = new WorkflowEngine(
            store,
            legacyRegistry,
            new ZhinuOptions
            {
                LeaseDuration = TimeSpan.FromMilliseconds(200),
                LeaseRenewalInterval = TimeSpan.FromMilliseconds(50),
                PollInterval = TimeSpan.FromMilliseconds(10)
            }))
        {
            runId = await legacyEngine.StartAsync("grouped-loop-signal", "1", "input", cancellationToken: Ct);
            using var interruption = new CancellationTokenSource();
            var execution = legacyEngine.ExecuteAsync(runId, interruption.Token);
            await WaitUntilAsync(
                () => HasStepStatusAsync(legacyEngine, runId, waitKey, StepStatus.Running, Ct), Ct);
            await interruption.CancelAsync();
            await execution;
            legacyWait = (await legacyEngine.GetStepsAsync(runId, Ct)).Single(step => step.StepKey == waitKey);
            legacyWait.Status.Should().Be(StepStatus.Running);
        }

        await using (var reopened = new WorkflowEngine(
            CreateStore(),
            Registry(invalidBinding: false, nativeUngrouped: true),
            new ZhinuOptions
            {
                LeaseDuration = TimeSpan.FromMilliseconds(200),
                LeaseRenewalInterval = TimeSpan.FromMilliseconds(50),
                PollInterval = TimeSpan.FromMilliseconds(10)
            }))
        {
            await reopened.SendSignalAsync(runId, "release", "accepted", Ct);
            await reopened.ExecuteAsync(runId, Ct);
            (await reopened.WaitForCompletionAsync<string>(runId, cancellationToken: Ct)).Should().Be("accepted");

            var recovered = (await reopened.GetStepsAsync(runId, Ct)).Single(step => step.StepKey == waitKey);
            recovered.Id.Should().Be(legacyWait.Id);
            recovered.Revision.Should().Be(legacyWait.Revision);
            recovered.InputJson.Should().Be(legacyWait.InputJson);
            recovered.InputHash.Should().Be(legacyWait.InputHash);
            recovered.Attempt.Should().BeGreaterThan(legacyWait.Attempt);
            recovered.Status.Should().Be(StepStatus.Completed);
            (await CreateStore().GetStepGroupMembershipAsync(recovered.Id, Ct)).Should().BeNull();
        }
    }

    private static WorkflowRegistry Registry(
        bool invalidBinding,
        ActivityGroupMemberRole? controlRole = null,
        bool legacyWrapper = false,
        bool nativeUngrouped = false) => new WorkflowRegistry().Register(
        new WorkflowRegistration<string, string>(
            new WorkflowDefinition { Name = "grouped-loop-signal", Version = "1" },
            () => new GroupedLoopSignalWorkflow(invalidBinding, controlRole, legacyWrapper, nativeUngrouped), GroupedLoopSignalWorkflow.Manifest()));

    private sealed class GroupedLoopSignalWorkflow(
        bool invalidBinding,
        ActivityGroupMemberRole? controlRole,
        bool legacyWrapper,
        bool nativeUngrouped) : IWorkflow<string, string>
    {
        public Task<string> RunAsync(WorkflowContext context, string input, CancellationToken cancellationToken) =>
            context.LoopAsync(
                "approval-loop",
                "waiting",
                static state => state == "waiting",
                async (iteration, ct) =>
                {
                    await iteration.StepAsync("prepare", (_, _) => Task.FromResult("ready"), cancellationToken: ct);
                    using var dependency = iteration.DependsOn("prepare");
                    var occurrence = invalidBinding
                        ? ActivityGroupOccurrence.Create(Guid.NewGuid(), Manifest(), "root", null, "[]")
                        : iteration.GetActivityGroup("work", context.GetActivityGroup("root"));
                    var binding = new ActivityGroupStepBinding(
                        new ActivityGroupMembership(invalidBinding ? "root" : "work"), occurrence);
                    var signal = legacyWrapper
                        ? await iteration.StepAsync<string, string>(
                            "approval",
                            "release",
                            (value, _, token) => context.WaitForSignalAsync<string>(
                                "$loop/approval-loop/1/body/approval", value, cancellationToken: token),
                            cancellationToken: ct)
                        : nativeUngrouped
                        ? await iteration.WaitForSignalAsync<string>("approval", "release", cancellationToken: ct)
                        : await iteration.WaitForGroupedSignalAsync<string>(
                            "approval", "release", binding, cancellationToken: ct);
                    return iteration.Break(signal);
                },
                new LoopOptions(maxIterations: 2)
                {
                    ControlGroup = controlRole is null
                        ? null
                        : new ActivityGroupStepBinding(
                            new ActivityGroupMembership("root", controlRole.Value),
                            context.GetActivityGroup("root"))
                },
                cancellationToken);

        internal static ActivityGroupManifest Manifest() => ActivityGroupManifest.Create([
            new ActivityGroupDefinition("root", ActivityGroupKind.Logical, "Loop"),
            new ActivityGroupDefinition("work", ActivityGroupKind.Logical, "Iteration work", "root")
        ]);
    }
}
