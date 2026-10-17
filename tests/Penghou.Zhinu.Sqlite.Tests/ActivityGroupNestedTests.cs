using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Penghou.Zhinu.Sqlite.ProcessProbe;

namespace Penghou.Zhinu.Sqlite.Tests;

public sealed class ActivityGroupNestedTests(ITestOutputHelper testOutput) : WorkflowEngineTestBase
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FixedThreeByTwo_ReconstructsSixLeavesAndCompositionalCompletion()
    {
        var store = CreateStore();
        var calls = new List<GroupNestedCall>();
        await using var engine = Engine(new GroupNestedWorkflow(callback: calls.Add));
        var id = await CompleteAsync(engine);
        var snapshot = await SnapshotAsync(store, id);
        await AssertCompleteAsync(store, id, snapshot);
        calls.Should().HaveCount(15);
        snapshot.Steps.Where(step => step.Key.EndsWith("/body/work", StringComparison.Ordinal))
            .Select(step => step.Input).Distinct().Should().ContainSingle("payload equality must not define occurrence identity");
        var dependencies = await engine.GetDependencyGraphAsync(id, Ct);
        for (var outer = 1; outer <= 3; outer++)
        {
            dependencies.Should().Contain(new StepDependency($"$loop/outer/{outer}/commit", $"$loop/outer/{outer}/loop/inner"));
            for (var inner = 1; inner <= 2; inner++)
                dependencies.Should().Contain(new StepDependency($"$loop/outer/{outer}/loop/inner/{inner}/commit",
                    $"$loop/outer/{outer}/loop/inner/{inner}/body/work"));
        }
        testOutput.WriteLine("NESTED_EXECUTION_EVIDENCE=" + JsonSerializer.Serialize(new { Snapshot = snapshot, Calls = calls, Dependencies = dependencies }));
    }

    [Theory]
    [InlineData("$loop/outer/2/loop/inner/2/body/work")]
    [InlineData("$loop/outer/2/body/entry")]
    public async Task ActivityRetry_PreservesFullLineageAndReusesCompletedSiblings(string retryKey)
    {
        var calls = new List<GroupNestedCall>();
        await using var engine = Engine(new GroupNestedWorkflow(callback: calls.Add, retryStep: retryKey));
        var id = await CompleteAsync(engine);
        var snapshot = await SnapshotAsync(CreateStore(), id);
        await AssertCompleteAsync(CreateStore(), id, snapshot);
        var retries = calls.Where(call => call.StepKey == retryKey).ToArray();
        retries.Should().HaveCount(2);
        retries.Select(call => call.Attempt).Should().Equal(1, 2);
        (retries[1] with { Attempt = 1 }).Should().Be(retries[0], "retry retains step revision, occurrence, parent and full coordinate");
        calls.Where(call => call.StepKey != retryKey).Select(call => call.StepKey).Should().OnlyHaveUniqueItems();
        snapshot.Steps.Single(step => step.Key == retryKey).Attempt.Should().Be(2);
        testOutput.WriteLine("NESTED_RETRY_EVIDENCE=" + JsonSerializer.Serialize(new { RetryKey = retryKey, Calls = calls, Snapshot = snapshot }));
    }

    [Theory]
    [InlineData("$loop/outer/2/loop/inner/2/body/work")]
    [InlineData("$loop/outer/2/commit")]
    public async Task SelectiveRestart_PreservesOccurrencesAndHistoricalMembership(string restartKey)
    {
        var store = CreateStore();
        Guid id;
        Snapshot before;
        HashSet<string> invalidated;
        await using (var engine = Engine(new GroupNestedWorkflow()))
        {
            id = await CompleteAsync(engine);
            before = await SnapshotAsync(store, id);
            var plan = await engine.RestartStepAsync(id, restartKey, Ct);
            invalidated = plan.StepsToInvalidate.Select(step => step.StepKey).ToHashSet(StringComparer.Ordinal);
            invalidated.Should().Contain("outer");
            invalidated.Should().NotContain("$loop/outer/1/commit");
        }
        var calls = new List<GroupNestedCall>();
        await using (var engine = Engine(new GroupNestedWorkflow(callback: calls.Add)))
        {
            await engine.ExecuteAsync(id, Ct);
            (await engine.WaitForCompletionAsync<string>(id, cancellationToken: Ct)).Should().Be("3");
        }
        var after = await SnapshotAsync(CreateStore(), id);
        await AssertCompleteAsync(store, id, after);
        after.Occurrences.Should().Equal(before.Occurrences);
        foreach (var step in before.Steps.Where(step => !invalidated.Contains(step.Key))) after.Steps.Should().Contain(step);
        calls.Should().OnlyContain(call => invalidated.Contains(call.StepKey));
        foreach (var prior in before.Memberships)
        {
            (await store.GetStepGroupMembershipAsync(prior.StepId, Ct)).Should().Be(prior);
            var current = after.Memberships.Single(item => item.StepKey == prior.StepKey);
            current.GroupOccurrenceId.Should().Be(prior.GroupOccurrenceId);
            if (invalidated.Contains(prior.StepKey)) current.StepRevision.Should().Be(prior.StepRevision + 1);
        }
        testOutput.WriteLine("NESTED_RESTART_EVIDENCE=" + JsonSerializer.Serialize(new { RestartKey = restartKey, Invalidated = invalidated, Calls = calls, Before = before, After = after }));
    }

    [Theory]
    [InlineData("outer-ready", 1, 0)]
    [InlineData("inner-ready", 2, 0)]
    [InlineData("early-running", 5, 2)]
    [InlineData("later-running", 10, 4)]
    [InlineData("leaf-completed", 3, 1)]
    [InlineData("inner-completed", 5, 2)]
    [InlineData("outer-committed", 5, 2)]
    public async Task ProcessLoss_FreshWorkersPreserveNestedIdentityAndDurableCompletion(string mode, int occurrences, int leaves)
    {
        var runPath = Path.Combine(root, "nested.run");
        (await WorkerAsync(runPath, mode)).Should().Be(93);
        var id = Guid.Parse(await File.ReadAllTextAsync(runPath, Ct));
        var store = CreateStore();
        var before = await SnapshotAsync(store, id);
        before.Status.Should().Be(WorkflowStatus.Running);
        before.Occurrences.Should().HaveCount(occurrences);
        before.Occurrences.Count(item => item.Key == "leaf").Should().Be(leaves);
        before.Steps.Should().NotContain(step => step.Key == "outer", "the outer loop cannot complete before its required iterations");
        if (mode is "outer-ready" or "inner-ready")
            before.Steps.Should().Contain(step => step.Key == "$loop/outer/1/body/entry" && step.Status == StepStatus.Completed);
        if (mode == "outer-ready")
            before.Steps.Should().NotContain(step => step.Key.StartsWith("$loop/outer/1/loop/inner", StringComparison.Ordinal));
        if (mode == "inner-ready")
            before.Steps.Should().Contain(step => step.Key == "$loop/outer/1/loop/inner/1/body/entry" && step.Status == StepStatus.Running);
        if (mode is "early-running" or "later-running")
        {
            var outer = mode == "early-running" ? 1 : 2;
            before.Steps.Should().Contain(step => step.Key == $"$loop/outer/{outer}/loop/inner/2/body/work" && step.Status == StepStatus.Running);
            before.Steps.Should().NotContain(step => step.Key == $"$loop/outer/{outer}/loop/inner");
            before.Steps.Should().NotContain(step => step.Key == $"$loop/outer/{outer}/commit");
        }
        if (mode == "leaf-completed")
            before.Steps.Should().NotContain(step => step.Key == "$loop/outer/1/loop/inner/1/commit");
        if (mode == "inner-completed")
        {
            before.Steps.Should().Contain(step => step.Key == "$loop/outer/1/loop/inner" && step.Status == StepStatus.Completed);
            before.Steps.Should().NotContain(step => step.Key == "$loop/outer/1/commit");
        }
        if (mode == "outer-committed")
        {
            before.Steps.Should().Contain(step => step.Key == "$loop/outer/1/commit" && step.Status == StepStatus.Completed);
            before.Steps.Should().NotContain(step => step.Key == "$loop/outer/2/condition");
        }

        // Recovery itself is a second new process. No fixture object, loop
        // scope, occurrence reference or cache survives either worker boundary.
        (await WorkerAsync(runPath, "recover")).Should().Be(0);
        var receipt = JsonSerializer.Deserialize<GroupNestedRecovery>(await File.ReadAllTextAsync(runPath + ".recovery", Ct))!;
        receipt.Result.Should().Be("3");
        var after = await SnapshotAsync(CreateStore(), id);
        await AssertCompleteAsync(CreateStore(), id, after);
        foreach (var prior in before.Occurrences) after.Occurrences.Should().Contain(prior);
        foreach (var prior in before.Memberships) after.Memberships.Should().Contain(prior);
        foreach (var prior in before.Steps.Where(step => step.Status == StepStatus.Completed)) after.Steps.Should().Contain(prior);
        foreach (var prior in before.Steps.Where(step => step.Status == StepStatus.Running))
        {
            var current = after.Steps.Single(step => step.Key == prior.Key);
            current.Id.Should().Be(prior.Id);
            current.Revision.Should().Be(prior.Revision);
            current.Attempt.Should().Be(prior.Attempt + 1);
        }
        var completedKeys = before.Steps.Where(step => step.Status == StepStatus.Completed).Select(step => step.Key).ToHashSet();
        receipt.Calls.Should().OnlyContain(call => !completedKeys.Contains(call.StepKey));
        receipt.Calls.Select(call => call.StepKey).Should().OnlyHaveUniqueItems();
        receipt.Calls.Count.Should().Be(15 - before.Memberships.Count(item => completedKeys.Contains(item.StepKey)));
        foreach (var call in receipt.Calls)
        {
            after.Memberships.Should().Contain(item => item.StepId == call.StepId && item.GroupOccurrenceId == call.Occurrence);
            after.Occurrences.Should().Contain(item => item.Id == call.Occurrence && item.Parent == call.Parent && item.Coordinate == call.Coordinate);
        }
        testOutput.WriteLine("NESTED_RECOVERY_EVIDENCE=" + JsonSerializer.Serialize(new { Mode = mode, CrashExit = 93, RecoveryExit = 0, Receipt = receipt, Before = before, After = after }));
    }

    private WorkflowEngine Engine(GroupNestedWorkflow workflow) => new(CreateStore(), GroupNestedWorkflow.Registry(workflow),
        new ZhinuOptions { PollInterval = TimeSpan.FromMilliseconds(10) });

    private static async Task<Guid> CompleteAsync(WorkflowEngine engine)
    {
        var id = await engine.StartAsync(GroupNestedWorkflow.Name, "1", "identical-input", cancellationToken: Ct);
        await engine.ExecuteAsync(id, Ct);
        (await engine.WaitForCompletionAsync<string>(id, cancellationToken: Ct)).Should().Be("3");
        return id;
    }

    private async Task<int> WorkerAsync(string runPath, string mode)
    {
        var output = new DirectoryInfo(AppContext.BaseDirectory);
        var probe = Path.Combine(output.Parent!.Parent!.Parent!.Parent!.FullName, "Penghou.Zhinu.Sqlite.ProcessProbe", "bin",
            output.Parent.Name, output.Name, "Penghou.Zhinu.Sqlite.ProcessProbe.dll");
        var start = new ProcessStartInfo { FileName = "dotnet", UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { probe, "--zhinu-group-nested-probe", Path.Combine(root, "zhinu.db"), runPath, mode }) start.ArgumentList.Add(argument);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        using var process = Process.Start(start)!;
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
        return process.ExitCode;
    }

    private static async Task AssertCompleteAsync(SqliteWorkflowStore store, Guid id, Snapshot snapshot)
    {
        snapshot.Status.Should().Be(WorkflowStatus.Completed);
        snapshot.Manifest.Should().Be(GroupNestedWorkflow.Manifest().CanonicalJson);
        snapshot.Occurrences.Should().HaveCount(15);
        snapshot.Occurrences.Select(item => item.Id).Should().OnlyHaveUniqueItems();
        snapshot.Memberships.Should().HaveCount(15);
        snapshot.Steps.Should().OnlyContain(step => step.Status == StepStatus.Completed);
        snapshot.Steps.Should().Contain(step => step.Key == "outer" && step.Output == "3");
        var leaves = snapshot.Occurrences.Where(item => item.Key == "leaf").ToArray();
        leaves.Should().HaveCount(6);
        var expectedCoordinates = new List<string>();
        for (var outer = 1; outer <= 3; outer++)
        {
            snapshot.Steps.Should().Contain(step => step.Key == $"$loop/outer/{outer}/commit");
            snapshot.Steps.Should().Contain(step => step.Key == $"$loop/outer/{outer}/loop/inner" && step.Output == "2");
            var outerCoordinate = $"[{{\"loop\":\"outer\",\"iteration\":{outer}}}]";
            var parent = snapshot.Occurrences.Single(item => item.Key == "outer" && item.Coordinate == outerCoordinate);
            parent.Parent.Should().BeNull();
            for (var inner = 1; inner <= 2; inner++)
            {
                var coordinate = $"[{{\"loop\":\"outer\",\"iteration\":{outer}}},{{\"loop\":\"inner\",\"iteration\":{inner}}}]";
                expectedCoordinates.Add(coordinate);
                var leaf = leaves.Single(item => item.Coordinate == coordinate);
                var immediate = await store.GetGroupOccurrenceAsync(id, leaf.Parent!, Ct);
                immediate.Should().NotBeNull();
                immediate!.GroupKey.Should().Be("inner");
                immediate.ExecutionCoordinate.Should().Be(coordinate);
                immediate.ParentGroupOccurrenceId.Should().Be(parent.Id);
                (await store.GetGroupOccurrenceAsync(id, immediate.ParentGroupOccurrenceId!, Ct))!.GroupOccurrenceId.Should().Be(parent.Id);
            }
        }
        leaves.Select(item => item.Coordinate).Should().BeEquivalentTo(expectedCoordinates);
        var events = await store.GetEventsAsync(id, 0, 1000, Ct);
        events.Count(item => item.EventType == WorkflowEventTypes.LoopIterationCommitted).Should().Be(
            snapshot.Steps.Where(step => step.Key.EndsWith("/commit", StringComparison.Ordinal)).Sum(step => step.Revision));
        events.Count(item => item.EventType == WorkflowEventTypes.LoopCompleted).Should().Be(
            snapshot.Steps.Where(step => step.Key == "outer" || step.Key.EndsWith("/loop/inner", StringComparison.Ordinal)).Sum(step => step.Revision),
            "three inner loop instances and the outer loop complete once per revision, including retained selective-restart history");
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
            for (var occurrenceId = membership.GroupOccurrenceId; occurrenceId is not null;)
            {
                var actual = (await store.GetGroupOccurrenceAsync(id, occurrenceId, Ct))!;
                actual.WorkflowRunId.Should().Be(id);
                occurrences.TryAdd(actual.GroupOccurrenceId, new Occurrence(actual.GroupOccurrenceId, actual.GroupKey,
                    actual.ParentGroupOccurrenceId, actual.ExecutionCoordinate));
                occurrenceId = actual.ParentGroupOccurrenceId;
            }
        }
        await using var connection = new SqliteConnection($"Data Source={Path.Combine(root, "zhinu.db")};Pooling=False");
        await connection.OpenAsync(Ct);
        await using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM workflow_group_occurrences WHERE workflow_run_id=$id;";
        count.Parameters.AddWithValue("$id", id.ToString("D"));
        Convert.ToInt32(await count.ExecuteScalarAsync(Ct)).Should().Be(occurrences.Count, "no orphan or duplicate occurrence can hide from point-read reconstruction");
        return new Snapshot((await store.GetRunAsync(id, Ct))!.Status, (await store.GetGroupManifestAsync(id, Ct))!.CanonicalJson,
            steps.Select(step => new Step(step.Id, step.StepKey, step.Revision, step.Attempt, step.Status, step.InputJson, step.OutputJson)).ToArray(),
            memberships, occurrences.Values.OrderBy(item => item.Id, StringComparer.Ordinal).ToArray());
    }

    private sealed record Snapshot(WorkflowStatus Status, string Manifest, IReadOnlyList<Step> Steps,
        IReadOnlyList<ActivityGroupStepMembership> Memberships, IReadOnlyList<Occurrence> Occurrences);
    private sealed record Step(Guid Id, string Key, int Revision, int Attempt, StepStatus Status, string? Input, string? Output);
    private sealed record Occurrence(string Id, string Key, string? Parent, string Coordinate);
}
