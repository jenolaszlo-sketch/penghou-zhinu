using System.Text.Json;
using FluentAssertions;
using Penghou.Zhinu.Sqlite.ProcessProbe;

namespace Penghou.Zhinu.Sqlite.Tests;

public sealed class ActivityGroupStateTests(ITestOutputHelper output) : WorkflowEngineTestBase
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task NestedSummaryFlattensMembersOnceAndReopensWithoutRegistry()
    {
        var runId = await CompleteNestedAsync();
        using var reader = new WorkflowGroupReader(CreateStore());
        var snapshot = (await reader.GetRunGroupSnapshotAsync(runId, Ct)).Value!;
        var roots = (await reader.QueryGroupChildrenAsync(runId, snapshot.SnapshotToken, cancellationToken: Ct)).Value!.Items.Where(node => node.Occurrence is not null).ToArray();
        var states = new List<WorkflowGroupStateSnapshot>();
        foreach (var root in roots)
        {
            var result = await reader.GetGroupStateAsync(runId, snapshot.SnapshotToken, root.Occurrence!.OccurrenceId, Ct);
            result.Status.Should().Be(WorkflowGroupReadStatus.Success);
            result.Value!.State.Status.Should().Be(StepStatus.Completed);
            result.Value.State.Work.Known.Should().Be(5, "one outer entry, two inner entries and two leaf steps are each counted once");
            result.Value.State.Work.Completed.Should().Be(5);
            result.Value.State.TotalCount.Should().Be(5);
            result.Value.ObservedAt.Should().Be(snapshot.CapturedAt);
            states.Add(result.Value);
        }
        states.Sum(state => state.State.Work.Known).Should().Be(15);
        output.WriteLine("GROUP_STATE_EVIDENCE " + JsonSerializer.Serialize(new { scenario = "nested-flattened-read-after-reopen", snapshot, states }));
    }

    [Theory]
    [InlineData(StepRestartMode.StepOnly)]
    [InlineData(StepRestartMode.Dependents)]
    public async Task RestartInvalidatesClosureWithoutOverridingNativeRestartPolicy(StepRestartMode mode)
    {
        var store = CreateStore();
        await using var engine = new WorkflowEngine(store, GroupNestedWorkflow.Registry(new GroupNestedWorkflow()));
        var runId = await engine.StartAsync(GroupNestedWorkflow.Name, "1", "input", cancellationToken: Ct);
        await engine.ExecuteAsync(runId, Ct);
        await engine.WaitForCompletionAsync<string>(runId, cancellationToken: Ct);
        using var reader = new WorkflowGroupReader(store);
        var complete = (await reader.GetRunGroupSnapshotAsync(runId, Ct)).Value!;
        var root = (await reader.QueryGroupChildrenAsync(runId, complete.SnapshotToken, cancellationToken: Ct)).Value!.Items.First(node => node.Occurrence is not null).Occurrence!;
        var before = (await reader.GetGroupStateAsync(runId, complete.SnapshotToken, root.OccurrenceId, Ct)).Value!;
        await engine.RestartStepAsync(runId, "$loop/outer/1/body/entry", new RestartStepOptions { Mode = mode }, Ct);
        var pending = (await reader.GetRunGroupSnapshotAsync(runId, Ct)).Value!;
        var after = (await reader.GetGroupStateAsync(runId, pending.SnapshotToken, root.OccurrenceId, Ct)).Value!;
        after.State.Status.Should().Be(StepStatus.Pending);
        after.State.TotalCount.Should().BeNull();
        after.State.Work.Known.Should().Be(before.State.Work.Known);
        after.State.Work.Pending.Should().BeGreaterThanOrEqualTo(1);
        (await reader.GetGroupStateAsync(runId, complete.SnapshotToken, root.OccurrenceId, Ct)).Value!.Should().BeEquivalentTo(before);
        await engine.ExecuteAsync(runId, Ct);
        await engine.WaitForCompletionAsync<string>(runId, cancellationToken: Ct);
        var recovered = (await reader.GetRunGroupSnapshotAsync(runId, Ct)).Value!;
        var final = (await reader.GetGroupStateAsync(runId, recovered.SnapshotToken, root.OccurrenceId, Ct)).Value!;
        // StepOnly deliberately leaves completed loop composites reusable. The
        // mechanical summary must still expose its untouched pending member.
        final.State.Status.Should().Be(mode == StepRestartMode.Dependents ? StepStatus.Completed : StepStatus.Pending);
        final.State.Work.Known.Should().Be(5);
        output.WriteLine("GROUP_STATE_EVIDENCE " + JsonSerializer.Serialize(new { scenario = "restart-invalidates-closure", before, after, final }));
    }

    [Fact]
    public async Task FanOutAdmissionProvidesPendingSelectionBeforeClaimWithoutClosingAncestor()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = CreateStore();
        await using var engine = new WorkflowEngine(store, Registry(new StateFanOutWorkflow(entered, release)));
        var runId = await engine.StartAsync("state-fanout", "1", "input", cancellationToken: Ct);
        var execution = engine.ExecuteAsync(runId, Ct);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(20), Ct);
            var clock = new FixedClock(DateTimeOffset.UtcNow);
            using var reader = new WorkflowGroupReader(CreateStore(), new WorkflowGroupQueryOptions { SnapshotLifetime = TimeSpan.FromHours(1) }, clock);
            var snapshot = (await reader.GetRunGroupSnapshotAsync(runId, Ct)).Value!;
            var expansion = (await reader.QueryGroupExpansionsAsync(runId, snapshot.SnapshotToken, cancellationToken: Ct)).Value!.Items.Single();
            var a = (await reader.GetGroupStateAsync(runId, snapshot.SnapshotToken, expansion.Items.Single(item => item.ItemKey == "a").OccurrenceId, Ct)).Value!;
            var b = (await reader.GetGroupStateAsync(runId, snapshot.SnapshotToken, expansion.Items.Single(item => item.ItemKey == "b").OccurrenceId, Ct)).Value!;
            a.State.Status.Should().Be(StepStatus.Running);
            a.State.TotalCount.Should().Be(1);
            a.State.Reasons.Should().NotContain(WorkflowGroupStateReason.AwaitingRecovery);
            clock.Now += TimeSpan.FromMinutes(30);
            var frozen = (await reader.GetGroupStateAsync(runId, snapshot.SnapshotToken, a.OccurrenceId, Ct)).Value!;
            frozen.Should().BeEquivalentTo(a);
            var laterSnapshot = (await reader.GetRunGroupSnapshotAsync(runId, Ct)).Value!;
            var later = (await reader.GetGroupStateAsync(runId, laterSnapshot.SnapshotToken, a.OccurrenceId, Ct)).Value!;
            later.State.Status.Should().Be(StepStatus.Running);
            later.State.Reasons.Should().Contain(WorkflowGroupStateReason.AwaitingRecovery);
            b.State.Status.Should().Be(StepStatus.Pending);
            b.State.Work.Pending.Should().Be(1);
            b.State.TotalCount.Should().Be(1);
            var root = (await reader.QueryGroupChildrenAsync(runId, snapshot.SnapshotToken, cancellationToken: Ct)).Value!.Items.Single(node => node.Occurrence is not null).Occurrence!;
            var parent = (await reader.GetGroupStateAsync(runId, snapshot.SnapshotToken, root.OccurrenceId, Ct)).Value!;
            parent.State.Work.Known.Should().Be(2);
            parent.State.Work.Running.Should().Be(1);
            parent.State.Work.Pending.Should().Be(1);
            parent.State.TotalCount.Should().BeNull();
            release.TrySetResult();
            await execution;
            await engine.WaitForCompletionAsync<string>(runId, cancellationToken: Ct);
            var complete = (await reader.GetRunGroupSnapshotAsync(runId, Ct)).Value!;
            var final = (await reader.GetGroupStateAsync(runId, complete.SnapshotToken, root.OccurrenceId, Ct)).Value!;
            final.State.Status.Should().Be(StepStatus.Completed);
            final.State.TotalCount.Should().Be(2);
            output.WriteLine("GROUP_STATE_EVIDENCE " + JsonSerializer.Serialize(new { scenario = "selected-before-claim", a, b, parent, final }));
        }
        finally { release.TrySetResult(); await execution; }
    }

    [Fact]
    public async Task HistoricalSelectionDoesNotAcquireCurrentProgressFromStaleClosure()
    {
        var store = CreateStore();
        await using var engine = new WorkflowEngine(store, Registry(new StateFanOutWorkflow()));
        var runId = await engine.StartAsync("state-fanout", "1", "input", cancellationToken: Ct);
        await engine.ExecuteAsync(runId, Ct);
        await engine.WaitForCompletionAsync<string>(runId, cancellationToken: Ct);
        using var reader = new WorkflowGroupReader(store);
        var snapshot = (await reader.GetRunGroupSnapshotAsync(runId, Ct)).Value!;
        var item = (await reader.QueryGroupExpansionsAsync(runId, snapshot.SnapshotToken, cancellationToken: Ct)).Value!.Items.Single().Items[0];
        await engine.RestartStepAsync(runId, "select", new RestartStepOptions { Mode = StepRestartMode.StepOnly }, Ct);
        var pending = (await reader.GetRunGroupSnapshotAsync(runId, Ct)).Value!;
        var history = (await reader.GetGroupStateAsync(runId, pending.SnapshotToken, item.OccurrenceId, Ct)).Value!;
        history.State.Status.Should().BeNull();
        history.State.Reason.Should().Be(WorkflowGroupStateReason.HistoricalSelection);
        history.State.TotalCount.Should().BeNull();
        history.State.Work.Known.Should().Be(0);
        var root = (await reader.QueryGroupChildrenAsync(runId, pending.SnapshotToken, cancellationToken: Ct)).Value!.Items.Single(node => node.Occurrence is not null).Occurrence!;
        var parent = (await reader.GetGroupStateAsync(runId, pending.SnapshotToken, root.OccurrenceId, Ct)).Value!;
        parent.State.Coverage.Should().Be(WorkflowGroupCoverage.Partial);
        parent.State.Status.Should().BeNull();
        output.WriteLine("GROUP_STATE_EVIDENCE " + JsonSerializer.Serialize(new { scenario = "historical-selection", history }));
    }

    [Fact]
    public async Task CancellationPreservesCompletedIndependentItemAndShowsMixedCounts()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = CreateStore();
        await using var engine = new WorkflowEngine(store, Registry(new StateFanOutWorkflow(entered, release, "b")));
        var runId = await engine.StartAsync("state-fanout", "1", "input", cancellationToken: Ct);
        var execution = engine.ExecuteAsync(runId, Ct);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(20), Ct);
            await engine.CancelAsync(runId, Ct);
            await execution;
            using var reader = new WorkflowGroupReader(CreateStore());
            var snapshot = (await reader.GetRunGroupSnapshotAsync(runId, Ct)).Value!;
            var expansion = (await reader.QueryGroupExpansionsAsync(runId, snapshot.SnapshotToken, cancellationToken: Ct)).Value!.Items.Single();
            var completed = (await reader.GetGroupStateAsync(runId, snapshot.SnapshotToken, expansion.Items.Single(item => item.ItemKey == "a").OccurrenceId, Ct)).Value!;
            completed.State.Status.Should().Be(StepStatus.Completed);
            completed.State.RunStatus.Should().Be(WorkflowStatus.Cancelled);
            var root = (await reader.QueryGroupChildrenAsync(runId, snapshot.SnapshotToken, cancellationToken: Ct)).Value!.Items.Single(node => node.Occurrence is not null).Occurrence!;
            var parent = (await reader.GetGroupStateAsync(runId, snapshot.SnapshotToken, root.OccurrenceId, Ct)).Value!;
            parent.State.Status.Should().Be(StepStatus.Cancelled);
            parent.State.Work.Completed.Should().Be(1);
            parent.State.Work.Cancelled.Should().Be(1);
            parent.State.TotalCount.Should().BeNull();
            output.WriteLine("GROUP_STATE_EVIDENCE " + JsonSerializer.Serialize(new { scenario = "cancellation-after-independent-completion", completed, parent }));
        }
        finally { release.TrySetResult(); await execution; }
    }

    [Fact]
    public async Task ChildTreeWorkNeverAddsToParentCounts()
    {
        var store = CreateStore();
        await using var engine = new WorkflowEngine(store, GroupChildWorkflow.Registry());
        var runId = await engine.StartAsync(GroupChildWorkflow.ParentName, "1", "input", cancellationToken: Ct);
        await engine.ExecuteAsync(runId, Ct);
        await engine.WaitForCompletionAsync<string>(runId, cancellationToken: Ct);
        using var reader = new WorkflowGroupReader(CreateStore());
        var snapshot = (await reader.GetRunGroupSnapshotAsync(runId, Ct)).Value!;
        var root = (await reader.QueryGroupChildrenAsync(runId, snapshot.SnapshotToken, cancellationToken: Ct)).Value!.Items.Single(node => node.Occurrence is not null).Occurrence!;
        var state = (await reader.GetGroupStateAsync(runId, snapshot.SnapshotToken, root.OccurrenceId, Ct)).Value!;
        state.State.Status.Should().Be(StepStatus.Completed);
        state.State.Work.Known.Should().Be(1);
        state.State.Control.Known.Should().Be(1);
        output.WriteLine("GROUP_STATE_EVIDENCE " + JsonSerializer.Serialize(new { scenario = "native-child-count-isolation", state }));
    }

    [Fact]
    public async Task SummaryReusesSnapshotAuthorizationAndExpiry()
    {
        var runId = await CompleteNestedAsync();
        var policy = new ReadPolicy();
        var clock = new FixedClock(DateTimeOffset.UtcNow);
        using var reader = new WorkflowGroupReader(CreateStore(), new WorkflowGroupQueryOptions { SnapshotLifetime = TimeSpan.FromSeconds(1) }, clock, policy);
        var snapshot = (await reader.GetRunGroupSnapshotAsync(runId, Ct)).Value!;
        var occurrence = (await reader.QueryGroupChildrenAsync(runId, snapshot.SnapshotToken, cancellationToken: Ct)).Value!.Items.First(node => node.Occurrence is not null).Occurrence!;
        (await reader.GetGroupStateAsync(runId, snapshot.SnapshotToken, occurrence.OccurrenceId, Ct)).Status.Should().Be(WorkflowGroupReadStatus.Success);
        policy.Access = WorkflowGroupReadAccess.None;
        (await reader.GetGroupStateAsync(runId, snapshot.SnapshotToken, occurrence.OccurrenceId, Ct)).Status.Should().Be(WorkflowGroupReadStatus.Forbidden);
        policy.Access = WorkflowGroupReadAccess.Structure;
        clock.Now += TimeSpan.FromSeconds(2);
        (await reader.GetGroupStateAsync(runId, snapshot.SnapshotToken, occurrence.OccurrenceId, Ct)).Status.Should().Be(WorkflowGroupReadStatus.ResyncRequired);
        using var other = new WorkflowGroupReader(CreateStore());
        (await other.GetGroupStateAsync(runId, snapshot.SnapshotToken, occurrence.OccurrenceId, Ct)).Status.Should().Be(WorkflowGroupReadStatus.ResyncRequired);
    }

    private async Task<Guid> CompleteNestedAsync()
    {
        await using var engine = new WorkflowEngine(CreateStore(), GroupNestedWorkflow.Registry(new GroupNestedWorkflow()));
        var runId = await engine.StartAsync(GroupNestedWorkflow.Name, "1", "input", cancellationToken: Ct);
        await engine.ExecuteAsync(runId, Ct);
        await engine.WaitForCompletionAsync<string>(runId, cancellationToken: Ct);
        return runId;
    }

    private static WorkflowRegistry Registry(StateFanOutWorkflow workflow) => new WorkflowRegistry().Register(new WorkflowRegistration<string, string>(
        new WorkflowDefinition { Name = "state-fanout", Version = "1" }, () => workflow,
        ActivityGroupManifest.Create([new ActivityGroupDefinition("root", ActivityGroupKind.Logical, "Root"),
            new ActivityGroupDefinition("work", ActivityGroupKind.Logical, "Work", "root")])));

    private sealed class StateFanOutWorkflow(TaskCompletionSource? entered = null, TaskCompletionSource? release = null, string blockedKey = "a") : IWorkflow<string, string>
    {
        public async Task<string> RunAsync(WorkflowContext context, string input, CancellationToken cancellationToken)
        {
            var selected = await context.StepAsync("select", input, (_, _) => Task.FromResult(new[] { "a", "b" }), cancellationToken: cancellationToken);
            var results = await context.FanOutAsync("items", selected, key => key, async (key, _, ct) =>
            {
                if (key == blockedKey && entered is not null && release is not null) { entered.TrySetResult(); await release.Task.WaitAsync(ct); }
                return key;
            }, new FanOutOptions { MaxDegreeOfParallelism = 1 }, new StepOptions
            {
                DependsOn = ["select"],
                FanOutGroup = new ActivityGroupFanOutBinding(new ActivityGroupMembership("work"), "select", context.GetActivityGroup("root"))
            }, cancellationToken);
            return string.Join(':', results);
        }
    }

    private sealed class ReadPolicy : IWorkflowGroupReadAuthorizer
    {
        public WorkflowGroupReadAccess Access { get; set; } = WorkflowGroupReadAccess.Structure;
        public ValueTask<WorkflowGroupReadAccess> AuthorizeAsync(Guid runId, CancellationToken cancellationToken = default) => ValueTask.FromResult(Access);
    }
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
