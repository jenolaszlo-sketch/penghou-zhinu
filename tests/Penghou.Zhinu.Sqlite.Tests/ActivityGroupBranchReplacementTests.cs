using FluentAssertions;
using Penghou.Zhinu.Sqlite.ProcessProbe;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using System.Reflection;

namespace Penghou.Zhinu.Sqlite.Tests;

public sealed class ActivityGroupBranchReplacementTests(ITestOutputHelper output) : WorkflowEngineTestBase
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task StoreWithoutReplacementCapabilityRejectsBeforeBranchDispatch()
    {
        var proxy = DispatchProxy.Create<INoReplacementStore, NoReplacementProxy>();
        ((NoReplacementProxy)(object)proxy).Store = CreateStore();
        var calls = new List<string>();
        await using var engine = new WorkflowEngine(proxy, GroupBranchWorkflow.Registry(new GroupBranchWorkflow("b") { OnCall = calls.Add }));
        var id = await engine.StartAsync("branch-replacement", "1", "input", cancellationToken: Ct);
        await engine.ExecuteAsync(id, Ct);
        var failure = await engine.Invoking(value => value.WaitForCompletionAsync<string>(id, cancellationToken: Ct))
            .Should().ThrowAsync<WorkflowExecutionFailedException>();
        failure.Which.Error.Message.Should().Contain("replacement repository capability");
        calls.Should().BeEmpty();
        (await CreateStore().ReadGroupSnapshotAsync(id, new(), Ct)).Value!.Expansions.Should().BeEmpty();
    }

    [Fact]
    public async Task MissingRetirementReceiptCannotSilentlyRemoveHistoricalObligations()
    {
        var runPath = Path.Combine(root, "branch.run");
        (await WorkerAsync(runPath, "seed")).Should().Be(93);
        var id = Guid.Parse(await File.ReadAllTextAsync(runPath, Ct));
        (await WorkerAsync(runPath, "partial")).Should().Be(93);
        (await WorkerAsync(runPath, "recover")).Should().Be(0);
        await AssertCurrentSelectionAsync(id);
        await using (var connection = new SqliteConnection($"Data Source={Path.Combine(root, "zhinu.db")};Pooling=False"))
        {
            await connection.OpenAsync(Ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM workflow_group_fanout_retirements;";
            await command.ExecuteNonQueryAsync(Ct);
        }
        var store = CreateStore();
        (await store.ReadGroupSnapshotAsync(id, new(), Ct)).Status.Should().Be(WorkflowGroupReadStatus.InconsistentData);
        using var reader = new WorkflowGroupReader(store);
        (await reader.GetRunGroupSnapshotAsync(id, Ct)).Status.Should().Be(WorkflowGroupReadStatus.InconsistentData);
        (await store.HasIncompleteGroupFanOutAsync(id, Ct)).Should().BeTrue();
    }

    public interface INoReplacementStore : IWorkflowStore, IWorkflowGroupRepository, IWorkflowGroupFanOutBodyRepository { }
    public class NoReplacementProxy : DispatchProxy
    {
        public SqliteWorkflowStore Store { get; set; } = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod!.Invoke(Store, args);
    }

    [Fact]
    public async Task CompletedBranchClosureSurvivesReplacementAndSameValueCanReappearInFreshScope()
    {
        Guid id;
        await using (var engine = CreateEngine(GroupBranchWorkflow.Registry(new GroupBranchWorkflow("a"))))
        {
            id = await engine.StartAsync("branch-replacement", "1", "input", cancellationToken: Ct);
            await engine.ExecuteAsync(id, Ct);
            (await engine.WaitForCompletionAsync<string>(id, cancellationToken: Ct)).Should().Be("a");
            await engine.RestartStepAsync(id, "select", new RestartStepOptions { Mode = StepRestartMode.StepOnly }, Ct);
        }
        var before = (await CreateStore().ReadGroupSnapshotAsync(id, new(), Ct)).Value!;
        var old = before.Expansions.Single();
        old.Closures.Should().ContainSingle();
        await using (var engine = CreateEngine(GroupBranchWorkflow.Registry(new GroupBranchWorkflow("b"))))
        {
            await engine.ExecuteAsync(id, Ct);
            (await engine.WaitForCompletionAsync<string>(id, cancellationToken: Ct)).Should().Be("b");
        }
        await AssertCurrentSelectionAsync(id);
        await using (var engine = CreateEngine(GroupBranchWorkflow.Registry(new GroupBranchWorkflow("a") { ScopeKey = "branch.a-again", Replaces = "branch.b" })))
        {
            await engine.RestartStepAsync(id, "select", new RestartStepOptions { Mode = StepRestartMode.StepOnly }, Ct);
            await engine.ExecuteAsync(id, Ct);
            (await engine.WaitForCompletionAsync<string>(id, cancellationToken: Ct)).Should().Be("a");
        }
        var store = CreateStore();
        var after = (await store.ReadGroupSnapshotAsync(id, new(), Ct)).Value!;
        after.Expansions.Should().HaveCount(3);
        after.Expansions.Single(expansion => expansion.ExpansionId == old.ExpansionId).Closures.Should().BeEquivalentTo(old.Closures);
        var firstA = after.Expansions.Single(expansion => expansion.FanOutKey == "branch.a").Items.Single();
        var secondA = after.Expansions.Single(expansion => expansion.FanOutKey == "branch.a-again").Items.Single();
        firstA.InputHash.Should().Be(secondA.InputHash);
        firstA.OccurrenceId.Should().NotBe(secondA.OccurrenceId);
        after.Expansions.Count(expansion => expansion.IsCurrent).Should().Be(1);
        foreach (var member in before.Steps.Where(step => step.Role == ActivityGroupMemberRole.Work))
            after.Steps.Single(step => step.StepId == member.StepId).Should().Be(member);
        using var reader = new WorkflowGroupReader(store);
        var snapshot = (await reader.GetRunGroupSnapshotAsync(id, Ct)).Value!;
        var rootId = after.Occurrences.Single(occurrence => occurrence.GroupKey == "root").OccurrenceId;
        var state = (await reader.GetGroupStateAsync(id, snapshot.SnapshotToken, rootId, Ct)).Value!.State;
        state.Status.Should().Be(StepStatus.Completed);
        state.TotalCount.Should().Be(2);
        state.Work.Known.Should().Be(2);
    }

    [Fact]
    public async Task ReplacementRetiresUnfinishedBranchWithoutRewritingCompletedHistory()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Guid id;
        WorkflowStepRun completed;
        await using (var engine = CreateEngine(GroupBranchWorkflow.Registry(new GroupBranchWorkflow("a") { Entered = entered })))
        {
            id = await engine.StartAsync("branch-replacement", "1", "input", cancellationToken: Ct);
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            var execution = engine.ExecuteAsync(id, stop.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(20), Ct);
            completed = (await engine.GetStepsAsync(id, Ct)).Single(step => step.StepKey.Contains("ZG9uZQ", StringComparison.Ordinal));
            completed.Status.Should().Be(StepStatus.Completed);
            stop.Cancel();
            try { await execution; } catch (OperationCanceledException) { }
            await engine.RestartStepAsync(id, "select", new RestartStepOptions { Mode = StepRestartMode.StepOnly }, Ct);
        }

        await using (var engine = CreateEngine(GroupBranchWorkflow.Registry(new GroupBranchWorkflow("b"))))
        {
            await engine.ExecuteAsync(id, Ct);
            (await engine.WaitForCompletionAsync<string>(id, cancellationToken: Ct)).Should().Be("b");
        }
        var steps = await CreateStore().GetStepsAsync(id, Ct);
        steps.Single(step => step.Id == completed.Id).Should().BeEquivalentTo(completed);
        steps.Where(step => step.StepKey.StartsWith("branch.a:body:", StringComparison.Ordinal)
            && step.Id != completed.Id).Should().ContainSingle().Which.Status.Should().Be(StepStatus.Cancelled);
        (await CreateStore().HasIncompleteGroupFanOutAsync(id, Ct)).Should().BeFalse();
        await AssertCurrentSelectionAsync(id);
    }

    [Theory]
    [InlineData("selection")]
    [InlineData("transaction")]
    [InlineData("admitted")]
    [InlineData("partial")]
    public async Task ProcessLossPreservesReplacementAndRetirementWithoutRecomputingSelection(string barrier)
    {
        var runPath = Path.Combine(root, "branch.run");
        (await WorkerAsync(runPath, "seed")).Should().Be(93);
        var id = Guid.Parse(await File.ReadAllTextAsync(runPath, Ct));
        var before = (await CreateStore().ReadGroupSnapshotAsync(id, new(), Ct)).Value!;
        var completed = before.Steps.Single(step => step.Status == StepStatus.Completed && step.Role == ActivityGroupMemberRole.Work);
        using var frozenReader = new WorkflowGroupReader(CreateStore());
        var frozen = (await frozenReader.GetRunGroupSnapshotAsync(id, Ct)).Value!;
        var rootId = before.Occurrences.Single(occurrence => occurrence.GroupKey == "root").OccurrenceId;
        var frozenState = (await frozenReader.GetGroupStateAsync(id, frozen.SnapshotToken, rootId, Ct)).Value!;

        (await WorkerAsync(runPath, barrier)).Should().Be(93);
        var interrupted = (await CreateStore().ReadGroupSnapshotAsync(id, new(), Ct)).Value!;
        var committed = barrier is "admitted" or "partial";
        interrupted.Expansions.Count.Should().Be(committed ? 2 : 1);
        interrupted.Expansions.Count(expansion => expansion.ReplacedByExpansionId is not null).Should().Be(committed ? 1 : 0);
        interrupted.Steps.Single(step => step.StepKey.StartsWith("branch.a:body:", StringComparison.Ordinal)
            && step.StepId != completed.StepId).Status.Should().Be(committed ? StepStatus.Cancelled : StepStatus.Waiting);
        (await WorkerAsync(runPath, "recover")).Should().Be(0);
        (await File.ReadAllTextAsync(runPath + ".result", Ct)).Should().Be("b", "today's selector must not replace the committed decision");
        var after = await AssertCurrentSelectionAsync(id);
        after.Steps.Single(step => step.StepId == completed.StepId).Should().Be(completed);
        foreach (var occurrence in before.Occurrences) after.Occurrences.Should().Contain(occurrence);
        foreach (var step in before.Steps.Where(step => step.GroupOccurrenceId is not null))
            after.Steps.Single(row => row.StepId == step.StepId).GroupOccurrenceId.Should().Be(step.GroupOccurrenceId);
        var calls = await File.ReadAllLinesAsync(runPath + ".calls", Ct);
        calls.Count(key => key == completed.StepKey).Should().Be(1);
        calls.Count(key => key.StartsWith("branch.a:body:", StringComparison.Ordinal)).Should().Be(2);
        var bDone = after.Steps.Single(step => step.StepKey.StartsWith("branch.b:body:", StringComparison.Ordinal)
            && step.StepKey.Contains("ZG9uZQ", StringComparison.Ordinal));
        calls.Count(key => key == bDone.StepKey).Should().Be(1);
        (await frozenReader.GetGroupStateAsync(id, frozen.SnapshotToken, rootId, Ct)).Value!.Should().BeEquivalentTo(frozenState);
        output.WriteLine("BRANCH_REPLACEMENT_EVIDENCE " + JsonSerializer.Serialize(new { barrier, before, interrupted, after, calls }));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("self")]
    public async Task InvalidReplacementRollsBackAdmissionBeforeAnyNewCallback(string invalid)
    {
        var runPath = Path.Combine(root, "branch.run");
        (await WorkerAsync(runPath, "seed")).Should().Be(93);
        var id = Guid.Parse(await File.ReadAllTextAsync(runPath, Ct));
        var store = CreateStore();
        await store.RecoverExpiredLeasesAsync(DateTimeOffset.UtcNow.AddMinutes(1), Ct);
        var calls = new List<string>();
        await using var engine = CreateEngine(GroupBranchWorkflow.Registry(new GroupBranchWorkflow("b")
        {
            Replaces = invalid == "missing" ? "absent" : "branch.b",
            OnCall = calls.Add
        }));
        await engine.RestartStepAsync(id, "select", new RestartStepOptions { Mode = StepRestartMode.StepOnly }, Ct);
        await engine.ExecuteAsync(id, Ct);
        await engine.Invoking(value => value.WaitForCompletionAsync<string>(id, cancellationToken: Ct))
            .Should().ThrowAsync<WorkflowExecutionFailedException>();
        calls.Should().BeEmpty();
        var snapshot = (await store.ReadGroupSnapshotAsync(id, new(), Ct)).Value!;
        snapshot.Expansions.Should().ContainSingle().Which.ReplacedByExpansionId.Should().BeNull();
        snapshot.Occurrences.Should().NotContain(occurrence => occurrence.ExecutionCoordinate.Contains("branch.b", StringComparison.Ordinal));
        (await engine.GetEventsAsync(id, cancellationToken: Ct)).Should().NotContain(e => e.EventType == WorkflowEventTypes.GroupFanOutReplaced);
    }

    [Fact]
    public async Task RetiredMembersCannotRestartAndDependentRestartDoesNotResurrectThem()
    {
        var runPath = Path.Combine(root, "branch.run");
        (await WorkerAsync(runPath, "seed")).Should().Be(93);
        var id = Guid.Parse(await File.ReadAllTextAsync(runPath, Ct));
        (await WorkerAsync(runPath, "partial")).Should().Be(93);
        (await WorkerAsync(runPath, "recover")).Should().Be(0);
        var before = await AssertCurrentSelectionAsync(id);
        var oldStep = before.Steps.First(step => step.StepKey.StartsWith("branch.a:body:", StringComparison.Ordinal));
        await using var engine = CreateEngine(GroupBranchWorkflow.Registry(new GroupBranchWorkflow("a")));
        await engine.Invoking(value => value.RestartStepAsync(id, oldStep.StepKey, Ct)).Should().ThrowAsync<WorkflowStateException>();
        var plan = await engine.RestartStepAsync(id, "select", new RestartStepOptions { Mode = StepRestartMode.Dependents }, Ct);
        plan.StepsToInvalidate.Should().NotContain(step => step.StepKey.StartsWith("branch.a", StringComparison.Ordinal));
        await engine.ExecuteAsync(id, Ct);
        var failure = await engine.Invoking(value => value.WaitForCompletionAsync<string>(id, cancellationToken: Ct))
            .Should().ThrowAsync<WorkflowExecutionFailedException>();
        failure.Which.Error.Message.Should().Contain("retired");
        var after = (await CreateStore().ReadGroupSnapshotAsync(id, new(), Ct)).Value!;
        after.Steps.Where(step => step.StepKey.StartsWith("branch.a", StringComparison.Ordinal)).Should()
            .BeEquivalentTo(before.Steps.Where(step => step.StepKey.StartsWith("branch.a", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task SchemaNineMigrationRetainsSelectionsAndMembershipWithoutInventingRetirement()
    {
        var runPath = Path.Combine(root, "branch.run");
        (await WorkerAsync(runPath, "seed")).Should().Be(93);
        var id = Guid.Parse(await File.ReadAllTextAsync(runPath, Ct));
        var before = (await CreateStore().ReadGroupSnapshotAsync(id, new(), Ct)).Value!;
        await using (var connection = new SqliteConnection($"Data Source={Path.Combine(root, "zhinu.db")};Pooling=False"))
        {
            await connection.OpenAsync(Ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE workflow_group_fanout_retirements; UPDATE zhinu_schema SET version=9 WHERE id=1;";
            await command.ExecuteNonQueryAsync(Ct);
        }
        var store = CreateStore();
        await store.InitializeAsync(Ct);
        await store.InitializeAsync(Ct);
        var after = (await store.ReadGroupSnapshotAsync(id, new(), Ct)).Value!;
        after.Should().BeEquivalentTo(before);
        (await WorkerAsync(runPath, "partial")).Should().Be(93);
        (await WorkerAsync(runPath, "recover")).Should().Be(0);
        await AssertCurrentSelectionAsync(id);
    }

    private async Task<WorkflowGroupSnapshotData> AssertCurrentSelectionAsync(Guid id)
    {
        var store = CreateStore();
        var data = (await store.ReadGroupSnapshotAsync(id, new(), Ct)).Value!;
        data.Run.Status.Should().Be(WorkflowStatus.Completed);
        var old = data.Expansions.Single(expansion => expansion.FanOutKey == "branch.a");
        var current = data.Expansions.Single(expansion => expansion.FanOutKey == "branch.b");
        old.ReplacedByExpansionId.Should().Be(current.ExpansionId);
        old.IsCurrent.Should().BeFalse();
        old.IsComplete.Should().BeFalse();
        current.IsCurrent.Should().BeTrue();
        current.IsComplete.Should().BeTrue();
        using var reader = new WorkflowGroupReader(store);
        var snapshot = (await reader.GetRunGroupSnapshotAsync(id, Ct)).Value!;
        var root = data.Occurrences.Single(occurrence => occurrence.GroupKey == "root");
        var parent = (await reader.GetGroupStateAsync(id, snapshot.SnapshotToken, root.OccurrenceId, Ct)).Value!.State;
        parent.Coverage.Should().Be(WorkflowGroupCoverage.Complete);
        parent.Status.Should().Be(StepStatus.Completed);
        parent.Work.Known.Should().Be(2);
        parent.Work.Completed.Should().Be(2);
        parent.TotalCount.Should().Be(2);
        foreach (var historical in data.Occurrences.Where(occurrence => occurrence.ExecutionCoordinate.Contains("branch.a", StringComparison.Ordinal)))
        {
            var state = (await reader.GetGroupStateAsync(id, snapshot.SnapshotToken, historical.OccurrenceId, Ct)).Value!.State;
            state.Reason.Should().Be(WorkflowGroupStateReason.HistoricalSelection);
            state.Status.Should().BeNull();
            state.Work.Known.Should().Be(0);
            state.TotalCount.Should().BeNull();
        }
        (await store.GetEventsAsync(id, 0, 1000, Ct)).Count(e => e.EventType == WorkflowEventTypes.GroupFanOutReplaced).Should().Be(1);
        (await store.HasIncompleteGroupFanOutAsync(id, Ct)).Should().BeFalse();
        return data;
    }

    private async Task<int> WorkerAsync(string runPath, string mode)
    {
        var outputDirectory = new DirectoryInfo(AppContext.BaseDirectory);
        var probe = Path.Combine(outputDirectory.Parent!.Parent!.Parent!.Parent!.FullName, "Penghou.Zhinu.Sqlite.ProcessProbe", "bin",
            outputDirectory.Parent.Name, outputDirectory.Name, "Penghou.Zhinu.Sqlite.ProcessProbe.dll");
        var start = new ProcessStartInfo { FileName = "dotnet", UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { probe, "--zhinu-group-branch-probe", Path.Combine(root, "zhinu.db"), runPath, mode }) start.ArgumentList.Add(arg);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        using var process = Process.Start(start)!;
        try { await process.WaitForExitAsync(timeout.Token); }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); }
        }
        return process.ExitCode;
    }
}
