using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Penghou.Zhinu.Sqlite.ProcessProbe;

namespace Penghou.Zhinu.Sqlite.Tests;

public sealed class ActivityGroupChildRecoveryTests(ITestOutputHelper testOutput) : WorkflowEngineTestBase
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("parent-claim")]
    [InlineData("child-manifest")]
    [InlineData("child-created")]
    [InlineData("start-completed")]
    [InlineData("wait-claim")]
    [InlineData("child-running")]
    [InlineData("child-completed")]
    public async Task ChildProcessBoundaries_RecoverWithSameChildAndGroupIdentity(string mode)
    {
        var store = CreateStore();
        var parentId = Guid.NewGuid();
        if (mode != "start-completed")
        {
            await using var starter = new WorkflowEngine(store, GroupChildWorkflow.Registry());
            await starter.StartAsync(
                GroupChildWorkflow.ParentName,
                "1",
                "probe",
                workflowRunId: parentId,
                cancellationToken: Ct);
        }

        var childId = ChildId(parentId);
        var process = await RunProbeAsync(mode, parentId);
        process.ExitCode.Should().Be(93, $"{mode} terminates at a durable child boundary");

        var parentSteps = await store.GetStepsAsync(parentId, Ct);
        var child = await store.GetRunAsync(childId, Ct);
        IReadOnlyList<WorkflowStepRun> childSteps = child is null
            ? Array.Empty<WorkflowStepRun>()
            : await store.GetStepsAsync(childId, Ct);
        var parentStart = parentSteps.SingleOrDefault(step => step.StepKey == "child:start");
        var parentWait = parentSteps.SingleOrDefault(step => step.StepKey == "child:wait");
        var events = await store.GetEventsAsync(parentId, 0, 100, Ct);
        var beforeParent = await SnapshotAsync(store, parentId);
        var beforeChild = child is null ? null : await SnapshotAsync(store, childId);

        switch (mode)
        {
            case "parent-claim":
                parentStart.Should().BeNull();
                child.Should().BeNull();
                beforeParent.Memberships.Should().BeEmpty();
                beforeParent.Occurrences.Should().BeEmpty();
                events.Should().NotContain(item => item.EventType == WorkflowEventTypes.StepStarted && item.StepKey == "child:start");
                break;
            case "child-manifest":
                parentStart!.Status.Should().Be(StepStatus.Running);
                await AssertParentMembershipAsync(store, parentId, parentStart);
                child.Should().BeNull("the child run, manifest, and started event share one admission transaction");
                break;
            case "child-created":
                parentStart!.Status.Should().Be(StepStatus.Running);
                child.Should().NotBeNull();
                child!.Status.Should().Be(WorkflowStatus.Pending);
                child.GroupManifestDigest.Should().Be(GroupChildWorkflow.Manifest().ManifestDigest);
                (await store.GetGroupManifestAsync(childId, Ct))!.CanonicalJson.Should().Be(GroupChildWorkflow.Manifest().CanonicalJson);
                childSteps.Should().BeEmpty();
                (await GenerationCountAsync(childId)).Should().Be(0, "the crash precedes initial generation creation");
                (await EventCountAsync(childId, WorkflowEventTypes.WorkflowStarted)).Should().Be(1);
                break;
            case "start-completed":
                parentStart!.Status.Should().Be(StepStatus.Completed);
                parentStart.OutputJson.Should().Be(JsonSerializer.Serialize(childId));
                child.Should().NotBeNull();
                child!.Id.Should().Be(childId);
                parentWait.Should().BeNull();
                await AssertParentMembershipAsync(store, parentId, parentStart);
                break;
            case "wait-claim":
                parentStart!.Status.Should().Be(StepStatus.Completed);
                parentWait.Should().BeNull("the wait membership and claim rolled back together");
                child.Should().NotBeNull();
                (await store.GetWaitAsync(parentId, "child:wait", Ct)).Should().BeNull();
                break;
            case "child-running":
                {
                    parentStart!.Status.Should().Be(StepStatus.Completed);
                    parentWait!.Status.Should().Be(StepStatus.Running);
                    child.Should().NotBeNull();
                    child!.Status.Should().Be(WorkflowStatus.Running);
                    var interruptedWork = childSteps.Single(step => step.StepKey == "work");
                    interruptedWork.Status.Should().Be(StepStatus.Running,
                        "the callback crash happens only after the child claim transaction committed");
                    var interruptedMembership = await store.GetStepGroupMembershipAsync(interruptedWork.Id, Ct);
                    interruptedMembership.Should().NotBeNull();
                    interruptedMembership!.WorkflowRunId.Should().Be(childId);
                    break;
                }
            case "child-completed":
                parentStart!.Status.Should().Be(StepStatus.Completed);
                parentWait!.Status.Should().Be(StepStatus.Running);
                child.Should().NotBeNull();
                child!.Status.Should().Be(WorkflowStatus.Completed);
                child.OutputJson.Should().Be(JsonSerializer.Serialize("probe:child"));
                (await store.GetEventsAsync(childId, 0, 50, Ct)).Should().ContainSingle(item => item.EventType == WorkflowEventTypes.WorkflowCompleted);
                break;
        }

        await DropTriggersAsync();
        var recovery = await RunProbeAsync("recover", parentId);
        recovery.ExitCode.Should().Be(0);
        var recoveryCallbacks = CallbackCount(recovery.ReceiptPath);
        recoveryCallbacks.Should().Be(mode == "child-completed" ? 0 : 1,
            "completed child work is reused; an unclaimed child operation runs in the recovery worker");

        var completedParent = (await store.GetRunAsync(parentId, Ct))!;
        completedParent.Status.Should().Be(WorkflowStatus.Completed);
        var finalSteps = await store.GetStepsAsync(parentId, Ct);
        var finalStart = finalSteps.Single(step => step.StepKey == "child:start");
        var finalWait = finalSteps.Single(step => step.StepKey == "child:wait");
        if (parentStart is not null)
            finalStart.Id.Should().Be(parentStart.Id);
        finalStart.OutputJson.Should().Be(JsonSerializer.Serialize(childId));
        finalWait.Status.Should().Be(StepStatus.Completed);
        finalWait.OutputJson.Should().Be(JsonSerializer.Serialize("probe:child"));
        await AssertParentMembershipAsync(store, parentId, finalStart);
        await AssertParentMembershipAsync(store, parentId, finalWait);
        var finalChild = (await store.GetRunAsync(childId, Ct))!;
        finalChild.ParentRunId.Should().Be(parentId);
        finalChild.GroupManifestDigest.Should().Be(GroupChildWorkflow.Manifest().ManifestDigest);
        finalChild.OutputJson.Should().Be(JsonSerializer.Serialize("probe:child"));
        (await GenerationCountAsync(childId)).Should().Be(1);
        (await store.GetRunSubtreeAsync(parentId, 5, Ct)).Select(run => run.Id)
            .Should().BeEquivalentTo(new[] { parentId, childId });
        (await store.GetGroupManifestAsync(childId, Ct))!.CanonicalJson.Should().Be(GroupChildWorkflow.Manifest().CanonicalJson);
        var finalWork = (await store.GetStepsAsync(childId, Ct)).Single(step => step.StepKey == "work");
        finalWork.Status.Should().Be(StepStatus.Completed);
        var finalMembership = await store.GetStepGroupMembershipAsync(finalWork.Id, Ct);
        finalMembership.Should().NotBeNull();
        finalMembership!.WorkflowRunId.Should().Be(childId);
        var afterParent = await SnapshotAsync(store, parentId);
        var afterChild = await SnapshotAsync(store, childId);
        AssertRetained(beforeParent, afterParent);
        if (beforeChild is not null) AssertRetained(beforeChild, afterChild);
        afterParent.Occurrences.Should().HaveCount(2);
        afterParent.Memberships.Should().HaveCount(2);
        afterChild.Occurrences.Should().HaveCount(2);
        afterChild.Memberships.Should().ContainSingle();
        afterParent.Occurrences.Select(item => item.Id).Should().NotIntersectWith(afterChild.Occurrences.Select(item => item.Id));
        testOutput.WriteLine("CHILD_RECOVERY_EVIDENCE=" + JsonSerializer.Serialize(new
        {
            Mode = mode,
            ParentId = parentId,
            ChildId = childId,
            ParentStart = finalStart,
            ParentWait = finalWait,
            ChildStatus = finalChild.Status,
            ChildManifestDigest = finalChild.GroupManifestDigest,
            RecoveryCallbacks = recoveryCallbacks,
            BeforeParent = beforeParent,
            BeforeChild = beforeChild,
            AfterParent = afterParent,
            AfterChild = afterChild,
            FreshProcessStarts = 2
        }));
    }

    [Fact]
    public async Task ParkedChildWait_ReadyAndConsumptionEachSurviveFreshProcessLoss()
    {
        var store = CreateStore();
        var parentId = Guid.NewGuid();
        var childId = ChildId(parentId);
        var start = await RunProbeAsync("start-completed", parentId);
        start.ExitCode.Should().Be(93);
        var parentStart = (await store.GetStepsAsync(parentId, Ct)).Single(step => step.StepKey == "child:start");
        parentStart.Status.Should().Be(StepStatus.Completed);
        var peer = StartProbe("peer-child-ready", parentId);
        var peerReceipt = peer.ReceiptPath;
        using var peerTimeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        peerTimeout.CancelAfter(TimeSpan.FromSeconds(45));
        try
        {
            await WaitUntilAsync(() => Task.FromResult(File.Exists(peerReceipt + ".started")), peerTimeout.Token);

            var parked = await RunProbeAsync("recover-parked-wait", parentId);
            parked.ExitCode.Should().Be(93);
            var wait = (await store.GetWaitAsync(parentId, "child:wait", Ct))!;
            wait.Status.Should().Be(WaitStatus.Parked);
            wait.ChildRunId.Should().Be(childId);
            (await store.GetRunAsync(childId, Ct))!.LeaseOwner.Should().NotBeNull();
            var waitingSteps = await store.GetStepsAsync(parentId, Ct);
            waitingSteps.Single(step => step.StepKey == "child:wait").Status.Should().Be(StepStatus.Running);
            var parkedParent = await SnapshotAsync(store, parentId);
            var runningChild = await SnapshotAsync(store, childId);

            File.WriteAllText(peerReceipt + ".release", "release");
            await peer.Process.WaitForExitAsync(peerTimeout.Token);
            peer.Process.ExitCode.Should().Be(93, "child terminal state and Ready wait commit before the injected process exit");
            (await store.GetRunAsync(childId, Ct))!.Status.Should().Be(WorkflowStatus.Completed);
            var readyWait = (await store.GetWaitAsync(parentId, "child:wait", Ct))!;
            readyWait.Status.Should().Be(WaitStatus.Ready);
            readyWait.WaitId.Should().Be(wait.WaitId);
            readyWait.StepId.Should().Be(wait.StepId);
            readyWait.StepRevision.Should().Be(wait.StepRevision);
            var completedChild = await SnapshotAsync(store, childId);
            CallbackCount(peerReceipt).Should().Be(1);

            var consumeCrash = await RunProbeAsync("recover-wait-completed", parentId);
            consumeCrash.ExitCode.Should().Be(93);
            (await store.GetWaitAsync(parentId, "child:wait", Ct))!.Status.Should().Be(WaitStatus.Completed);
            (await store.GetStepsAsync(parentId, Ct)).Single(step => step.StepKey == "child:wait").Status.Should().Be(StepStatus.Completed);
            var completedWaitParent = await SnapshotAsync(store, parentId);
            CallbackCount(consumeCrash.ReceiptPath).Should().Be(0);

            var finalRecovery = await RunProbeAsync("recover", parentId);
            finalRecovery.ExitCode.Should().Be(0);
            CallbackCount(finalRecovery.ReceiptPath).Should().Be(0);
            (await store.GetRunAsync(parentId, Ct))!.Status.Should().Be(WorkflowStatus.Completed);
            (await store.GetWaitAsync(parentId, "child:wait", Ct))!.Status.Should().Be(WaitStatus.Completed);
            (await store.GetStepsAsync(parentId, Ct)).Single(step => step.StepKey == "child:wait").Status.Should().Be(StepStatus.Completed);
            var finalParent = await SnapshotAsync(store, parentId);
            var finalChild = await SnapshotAsync(store, childId);
            AssertRetained(parkedParent, finalParent);
            AssertRetained(runningChild, finalChild);
            AssertRetained(completedWaitParent, finalParent);
            finalChild.Should().BeEquivalentTo(completedChild);
            (await store.GetRunSubtreeAsync(parentId, 5, Ct)).Select(run => run.Id)
                .Should().BeEquivalentTo(new[] { parentId, childId });
            testOutput.WriteLine("CHILD_RECOVERY_EVIDENCE=" + JsonSerializer.Serialize(new
            {
                ParentId = parentId,
                ChildId = childId,
                Mode = "parked-ready-wait-completed",
                ParkedWait = wait,
                ReadyWait = readyWait,
                ParkedParent = parkedParent,
                RunningChild = runningChild,
                CompletedChild = completedChild,
                CompletedWaitParent = completedWaitParent,
                FinalParent = finalParent,
                FinalChild = finalChild,
                PeerCallbackCount = CallbackCount(peerReceipt),
                ParentAfterRecovery = await store.GetRunAsync(parentId, Ct),
                FreshProcessStarts = 5
            }));
        }
        finally
        {
            if (!peer.Process.HasExited)
            {
                File.WriteAllText(peerReceipt + ".release", "release");
                peer.Process.Kill(entireProcessTree: true);
                await peer.Process.WaitForExitAsync(CancellationToken.None);
            }
            peer.Process.Dispose();
        }
    }

    private async Task<ProbeResult> RunProbeAsync(string mode, Guid parentId)
    {
        var (process, receipt) = StartProbe(mode, parentId);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        try
        {
            await WaitUntilAsync(() => Task.FromResult(File.Exists(receipt)), timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            return new ProbeResult(process.ExitCode, receipt);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
            process.Dispose();
        }
    }

    private (Process Process, string ReceiptPath) StartProbe(string mode, Guid parentId)
    {
        var receipt = Path.Combine(root, $"child-{mode}-{Guid.NewGuid():N}.receipt");
        var start = new ProcessStartInfo { FileName = "dotnet", UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { ProcessProbePath(), "--zhinu-group-child-probe", Path.Combine(root, "zhinu.db"), parentId.ToString("D"), mode, receipt })
            start.ArgumentList.Add(arg);
        var process = Process.Start(start);
        process.Should().NotBeNull();
        return (process!, receipt);
    }

    private async Task AssertParentMembershipAsync(SqliteWorkflowStore store, Guid parentId, WorkflowStepRun step)
    {
        var membership = await store.GetStepGroupMembershipAsync(step.Id, Ct);
        membership.Should().NotBeNull();
        membership!.WorkflowRunId.Should().Be(parentId);
        membership.StepId.Should().Be(step.Id);
        membership.StepKey.Should().Be(step.StepKey);
        membership.StepRevision.Should().Be(step.Revision);
        var manifest = GroupChildWorkflow.Manifest();
        var root = ActivityGroupOccurrence.Create(parentId, manifest, "root", null, "[]");
        var macro = ActivityGroupOccurrence.Create(parentId, manifest, "macro", root, "[]");
        membership.GroupOccurrenceId.Should().Be(macro.GroupOccurrenceId);
        membership.Role.Should().Be(step.StepKey == "child:start"
            ? ActivityGroupMemberRole.Control
            : ActivityGroupMemberRole.Work);
    }

    private async Task<Snapshot> SnapshotAsync(SqliteWorkflowStore store, Guid id)
    {
        var steps = (await store.GetStepsAsync(id, Ct)).OrderBy(step => step.StepKey, StringComparer.Ordinal).ToArray();
        var memberships = new List<ActivityGroupStepMembership>();
        var occurrences = new Dictionary<string, Occurrence>(StringComparer.Ordinal);
        foreach (var step in steps)
        {
            var membership = await store.GetStepGroupMembershipAsync(step.Id, Ct);
            if (membership is null) continue;
            memberships.Add(membership);
            membership.WorkflowRunId.Should().Be(id);
            membership.StepRevision.Should().Be(step.Revision);
            for (var occurrenceId = membership.GroupOccurrenceId; occurrenceId is not null;)
            {
                var occurrence = (await store.GetGroupOccurrenceAsync(id, occurrenceId, Ct))!;
                occurrence.Should().NotBeNull();
                occurrence.WorkflowRunId.Should().Be(id);
                occurrence.ExecutionCoordinate.Should().Be("[]");
                occurrences.TryAdd(occurrenceId, new Occurrence(occurrenceId, occurrence.GroupKey, occurrence.ParentGroupOccurrenceId, occurrence.ExecutionCoordinate));
                occurrenceId = occurrence.ParentGroupOccurrenceId;
            }
        }
        await using var connection = await OpenAsync();
        await using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM workflow_group_occurrences WHERE workflow_run_id=$id;";
        count.Parameters.AddWithValue("$id", id.ToString("D"));
        Convert.ToInt32(await count.ExecuteScalarAsync(Ct)).Should().Be(occurrences.Count,
            "no orphan or duplicate occurrence may hide outside the exact step membership ancestry");
        return new Snapshot(id, (await store.GetRunAsync(id, Ct))!.Status,
            (await store.GetGroupManifestAsync(id, Ct))!.CanonicalJson,
            steps.Select(step => new Step(step.Id, step.StepKey, step.Revision, step.Attempt, step.Status, step.InputJson, step.OutputJson)).ToArray(),
            memberships, occurrences.Values.OrderBy(item => item.Id, StringComparer.Ordinal).ToArray());
    }

    private static void AssertRetained(Snapshot before, Snapshot after)
    {
        after.Manifest.Should().Be(before.Manifest);
        foreach (var member in before.Memberships) after.Memberships.Should().ContainEquivalentOf(member);
        foreach (var occurrence in before.Occurrences) after.Occurrences.Should().ContainEquivalentOf(occurrence);
        foreach (var step in before.Steps)
        {
            var current = after.Steps.Single(item => item.Id == step.Id);
            current.Key.Should().Be(step.Key);
            current.Revision.Should().Be(step.Revision);
            current.Input.Should().Be(step.Input);
            if (step.Status == StepStatus.Completed) current.Should().Be(step);
            else current.Attempt.Should().BeGreaterThanOrEqualTo(step.Attempt);
        }
    }

    private sealed record Snapshot(Guid RunId, WorkflowStatus Status, string Manifest, IReadOnlyList<Step> Steps,
        IReadOnlyList<ActivityGroupStepMembership> Memberships, IReadOnlyList<Occurrence> Occurrences);
    private sealed record Step(Guid Id, string Key, int Revision, int Attempt, StepStatus Status, string? Input, string? Output);
    private sealed record Occurrence(string Id, string Key, string? Parent, string Coordinate);

    private async Task<int> GenerationCountAsync(Guid runId)
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM workflow_generations WHERE workflow_run_id=$id;";
        command.Parameters.AddWithValue("$id", runId.ToString("D"));
        return Convert.ToInt32(await command.ExecuteScalarAsync(Ct));
    }

    private async Task<int> EventCountAsync(Guid runId, string eventType)
    {
        var events = await CreateStore().GetEventsAsync(runId, 0, 100, Ct);
        return events.Count(item => item.EventType == eventType);
    }

    private async Task<SqliteConnection> OpenAsync()
    {
        var connection = new SqliteConnection($"Data Source={Path.Combine(root, "zhinu.db")};Pooling=False");
        await connection.OpenAsync(Ct);
        return connection;
    }

    private async Task DropTriggersAsync()
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DROP TRIGGER IF EXISTS zhinu_exit_parent_start_claim;
            DROP TRIGGER IF EXISTS zhinu_exit_child_manifest;
            DROP TRIGGER IF EXISTS zhinu_exit_child_generation;
            DROP TRIGGER IF EXISTS zhinu_exit_parent_wait_claim;
            """;
        await command.ExecuteNonQueryAsync(Ct);
    }

    private static Guid ChildId(Guid parentId) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes($"{parentId:D}:child:1")).AsSpan(0, 16));

    private static ZhinuOptions EngineOptions() => new()
    {
        PollInterval = TimeSpan.FromMilliseconds(10),
        LeaseDuration = TimeSpan.FromSeconds(3),
        LeaseRenewalInterval = TimeSpan.FromMilliseconds(800)
    };

    private static string ProcessProbePath()
    {
        var output = new DirectoryInfo(AppContext.BaseDirectory);
        return Path.Combine(output.Parent!.Parent!.Parent!.Parent!.FullName,
            "Penghou.Zhinu.Sqlite.ProcessProbe", "bin", output.Parent.Name, output.Name,
            "Penghou.Zhinu.Sqlite.ProcessProbe.dll");
    }

    private static int CallbackCount(string receiptPath) =>
        File.Exists(receiptPath + ".callback")
            ? File.ReadAllLines(receiptPath + ".callback").Length
            : 0;

    private sealed record ProbeResult(int ExitCode, string ReceiptPath);
}
