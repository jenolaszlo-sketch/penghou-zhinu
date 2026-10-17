using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Penghou.Zhinu.Sqlite.ProcessProbe;

namespace Penghou.Zhinu.Sqlite.Tests;

public sealed class ActivityGroupSelectionTests(ITestOutputHelper testOutput) : WorkflowEngineTestBase
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EvolvingEpochs_ExpandContractReorderAndReappearWithoutReinterpretingHistory(bool positional)
    {
        var calls = new ConcurrentQueue<SelectionCall>();
        var selectors = new List<int>();
        await using var engine = Engine(new GroupSelectionWorkflow { Positional = positional, OnCall = calls.Enqueue, OnSelection = selectors.Add });
        var id = await CompleteAsync(engine);
        var snapshot = await SnapshotAsync(id);
        await AssertCompleteAsync(id, snapshot);
        selectors.Should().Equal(1, 2, 3, 4, 5);
        calls.Should().HaveCount(14);
        snapshot.Selected.Single(epoch => epoch.Epoch == 1).Items.Select(item => item.Key).Should().Equal("A", "B", "C");
        snapshot.Selected.Single(epoch => epoch.Epoch == 2).Items.Select(item => item.Key).Should().Equal("A", "C", "D", "E");
        snapshot.Selected.Single(epoch => epoch.Epoch == 3).Items.Select(item => item.Key).Should().Equal("C", "A");
        snapshot.Selected.Single(epoch => epoch.Epoch == 4).Items.Select(item => item.Key).Should().Equal("A", "C");
        var oldB = snapshot.Items.Single(item => item.Scope == "items.1" && item.Key == (positional ? "1" : "B"));
        var newB = snapshot.Items.Single(item => item.Scope == "items.5" && item.Key == (positional ? "0" : "B"));
        oldB.Occurrence.Should().NotBe(newB.Occurrence);
        oldB.StepKey.Should().NotBe(newB.StepKey);
        oldB.InputHash.Should().Be(newB.InputHash, "the same B value can reappear under a distinct epoch identity");
        testOutput.WriteLine("SELECTION_EXECUTION_EVIDENCE=" + JsonSerializer.Serialize(new { Positional = positional, Calls = calls, Snapshot = snapshot }));
    }

    [Fact]
    public async Task Retry_ReplaysHistoricalSelectionInsteadOfRecomputingIt()
    {
        var calls = new ConcurrentQueue<SelectionCall>();
        var selectors = new List<int>();
        await using var engine = Engine(new GroupSelectionWorkflow { RetryStep = "items.5.B", OnCall = calls.Enqueue, OnSelection = selectors.Add });
        var id = await CompleteAsync(engine);
        var snapshot = await SnapshotAsync(id);
        await AssertCompleteAsync(id, snapshot);
        selectors.Should().Equal(1, 2, 3, 4, 5);
        var retries = calls.Where(call => call.StepKey == "items.5.B").ToArray();
        retries.Should().HaveCount(2);
        retries.Select(call => call.Attempt).Should().Equal(1, 2);
        (retries[1] with { Attempt = 1 }).Should().Be(retries[0]);
        calls.Where(call => call.StepKey != "items.5.B").Select(call => call.StepKey).Should().OnlyHaveUniqueItems();
        testOutput.WriteLine("SELECTION_RETRY_EVIDENCE=" + JsonSerializer.Serialize(new { Calls = calls, Snapshot = snapshot }));
    }

    [Theory]
    [InlineData("items.1.B")]
    [InlineData("items.2.D")]
    public async Task SelectiveRestart_ReusesStoredEpochsDespiteChangedCurrentSelectors(string restartKey)
    {
        Guid id;
        Snapshot before;
        await using (var engine = Engine(new GroupSelectionWorkflow()))
        {
            id = await CompleteAsync(engine);
            before = await SnapshotAsync(id);
            await engine.RestartStepAsync(id, restartKey, new RestartStepOptions { Mode = StepRestartMode.StepOnly }, Ct);
        }
        var calls = new ConcurrentQueue<SelectionCall>();
        var selectors = new List<int>();
        var schedules = new List<int[]>();
        await using (var engine = Engine(new GroupSelectionWorkflow
        {
            CurrentSelectors = true,
            OnCall = calls.Enqueue,
            OnSelection = selectors.Add,
            OnSchedule = schedules.Add
        }))
        {
            await engine.ExecuteAsync(id, Ct);
            (await engine.WaitForCompletionAsync<string>(id, cancellationToken: Ct)).Should().Be("five-epochs-complete");
        }
        var after = await SnapshotAsync(id);
        await AssertCompleteAsync(id, after);
        selectors.Should().BeEmpty();
        schedules.Should().BeEmpty();
        calls.Select(call => call.StepKey).Should().Equal(restartKey);
        after.Occurrences.Should().Equal(before.Occurrences);
        after.Expansions.Should().Equal(before.Expansions);
        after.Items.Should().Equal(before.Items);
        foreach (var prior in before.Closures) after.Closures.Should().Contain(prior);
        foreach (var prior in before.Memberships)
        {
            (await CreateStore().GetStepGroupMembershipAsync(prior.StepId, Ct)).Should().Be(prior);
            after.Memberships.Single(item => item.StepKey == prior.StepKey).GroupOccurrenceId.Should().Be(prior.GroupOccurrenceId);
        }
        foreach (var prior in before.Steps.Where(step => step.Key != restartKey)) after.Steps.Should().Contain(prior);
        testOutput.WriteLine("SELECTION_RESTART_EVIDENCE=" + JsonSerializer.Serialize(new { RestartKey = restartKey, Before = before, After = after, Calls = calls }));
    }

    [Fact]
    public async Task OmittingUnfinishedHistoricalEpoch_CannotCompleteTheRun()
    {
        var runPath = Path.Combine(root, "epochs.run");
        (await WorkerAsync(runPath, "partial")).Should().Be(93);
        var id = Guid.Parse(await File.ReadAllTextAsync(runPath, Ct));
        var before = await SnapshotAsync(id);
        (await WorkerAsync(runPath, "recover-omit")).Should().Be(0);
        var receipt = JsonSerializer.Deserialize<SelectionOmission>(await File.ReadAllTextAsync(runPath + ".omission", Ct))!;
        receipt.Status.Should().Be(WorkflowStatus.Failed);
        receipt.ErrorType.Should().Be(typeof(WorkflowConfigurationException).FullName);
        receipt.ErrorMessage.Should().Be("Workflow completed with an incomplete grouped fan-out enumeration.");
        (await CreateStore().HasIncompleteGroupFanOutAsync(id, Ct)).Should().BeTrue();
        var after = await SnapshotAsync(id);
        foreach (var prior in before.Items) after.Items.Should().Contain(prior);
        foreach (var prior in before.Occurrences) after.Occurrences.Should().Contain(prior);
        (await CreateStore().GetGroupFanOutAsync(id, "items.1", Ct))!.IsComplete.Should().BeFalse();
        testOutput.WriteLine("SELECTION_OBLIGATION_EVIDENCE=" + JsonSerializer.Serialize(new { CrashExit = 93, RecoveryExit = 0, Before = before, After = after, Receipt = receipt }));
    }

    [Theory]
    [InlineData("expand")]
    [InlineData("contract")]
    [InlineData("value-drift")]
    [InlineData("positional-reorder")]
    public async Task RewritingAnOldSelectionRevision_RejectsWithoutReinterpretingItsEpoch(string change)
    {
        var positional = change == "positional-reorder";
        Guid id;
        Snapshot before;
        await using (var engine = Engine(new GroupSelectionWorkflow { Positional = positional }))
        {
            id = await CompleteAsync(engine);
            before = await SnapshotAsync(id);
            await engine.RestartStepAsync(id, "select.1", new RestartStepOptions { Mode = StepRestartMode.StepOnly }, Ct);
        }
        var pending = await SnapshotAsync(id);
        pending.States.Single(state => state.FanOutKey == "items.1").IsComplete.Should().BeFalse();
        (await CreateStore().HasIncompleteGroupFanOutAsync(id, Ct)).Should().BeTrue();
        pending.Expansions.Should().Equal(before.Expansions);
        pending.Closures.Should().Equal(before.Closures);
        var calls = new ConcurrentQueue<SelectionCall>();
        SelectionItem[] Select(int epoch)
        {
            var original = GroupSelectionWorkflow.Items(epoch);
            if (epoch != 1) return original;
            return change switch
            {
                "expand" => [.. original, new SelectionItem("D", "identical-payload")],
                "contract" => original.Where(item => item.Key != "B").ToArray(),
                "value-drift" => original.Select(item => item.Key == "B" ? item with { Payload = "changed" } : item).ToArray(),
                _ => Enumerable.Reverse(original).ToArray()
            };
        }
        await using (var engine = Engine(new GroupSelectionWorkflow { Positional = positional, SelectionOverride = Select, OnCall = calls.Enqueue }))
        {
            await engine.ExecuteAsync(id, Ct);
            var result = () => engine.WaitForCompletionAsync<string>(id, cancellationToken: Ct);
            var failure = await result.Should().ThrowAsync<WorkflowExecutionFailedException>();
            failure.Which.Error.Message.Should().Contain("same selection");
        }
        var after = await SnapshotAsync(id);
        calls.Should().BeEmpty();
        after.Expansions.Should().Equal(before.Expansions);
        after.Items.Should().Equal(before.Items);
        after.Occurrences.Should().Equal(before.Occurrences);
        after.Memberships.Should().Equal(before.Memberships);
        after.Closures.Should().Equal(before.Closures);
        // The previous selector revision is still durable, including its order.
        await using var connection = new SqliteConnection($"Data Source={Path.Combine(root, "zhinu.db")};Pooling=False");
        await connection.OpenAsync(Ct);
        await using var old = connection.CreateCommand();
        old.CommandText = "SELECT output_json FROM workflow_steps WHERE workflow_run_id=$id AND step_key='select.1' AND revision=1;";
        old.Parameters.AddWithValue("$id", id.ToString("D"));
        ((string?)await old.ExecuteScalarAsync(Ct)).Should().Be(before.Steps.Single(step => step.Key == "select.1").Output);
        testOutput.WriteLine("SELECTION_REJECTION_EVIDENCE=" + JsonSerializer.Serialize(new { Change = change, Before = before, Pending = pending, After = after }));
    }

    [Theory]
    [InlineData("selection", 1)]
    [InlineData("admit-transaction", 1)]
    [InlineData("admitted", 2)]
    [InlineData("contracted-selection", 2)]
    [InlineData("partial", 3)]
    [InlineData("obligations-completed", 3)]
    [InlineData("reordered", 4)]
    [InlineData("reappeared", 5)]
    public async Task ProcessLoss_RestoresImmutableSelectionHistoryBeforeContinuingWithCurrentSelectors(string mode, int expansions)
    {
        var runPath = Path.Combine(root, "epochs.run");
        (await WorkerAsync(runPath, mode)).Should().Be(93);
        var id = Guid.Parse(await File.ReadAllTextAsync(runPath, Ct));
        var before = await SnapshotAsync(id);
        before.Status.Should().Be(WorkflowStatus.Running);
        before.Expansions.Should().HaveCount(expansions);
        if (mode is "selection" or "admit-transaction")
        {
            before.Selected.Should().Contain(item => item.Epoch == 2);
            before.Items.Should().NotContain(item => item.Scope == "items.2");
            before.Occurrences.Should().HaveCount(4, "interrupted epoch 2 admission cannot leave a prefix of occurrences/items behind");
        }
        if (mode == "admitted")
        {
            before.Items.Count(item => item.Scope == "items.2").Should().Be(4);
            before.Steps.Should().NotContain(step => step.Key.StartsWith("items.2.", StringComparison.Ordinal));
            before.Occurrences.Should().HaveCount(8);
        }
        if (mode is "contracted-selection" or "partial")
        {
            before.Selected.Single(item => item.Epoch == 3).Items.Select(item => item.Key).Should().Equal("C", "A");
            before.Steps.Should().Contain(step => step.Key == "items.1.B" && step.Status == StepStatus.Completed);
            before.Steps.Should().Contain(step => step.Key == "items.1.C" && step.Status == StepStatus.Running);
            before.Steps.Should().Contain(step => step.Key == "items.2.D" && step.Status == StepStatus.Running);
            before.Steps.Should().NotContain(step => step.Key == "items.2.E");
            (await CreateStore().HasIncompleteGroupFanOutAsync(id, Ct)).Should().BeTrue();
        }
        if (mode is "obligations-completed" or "reordered" or "reappeared")
            before.States.Should().OnlyContain(state => state.IsComplete);

        (await WorkerAsync(runPath, "recover")).Should().Be(0);
        var receipt = JsonSerializer.Deserialize<SelectionRecovery>(await File.ReadAllTextAsync(runPath + ".recovery", Ct))!;
        receipt.Result.Should().Be("five-epochs-complete");
        receipt.Schedules.Should().BeEmpty("the epoch identities themselves are durable");
        receipt.Selectors.Should().BeEquivalentTo(Enumerable.Range(1, 5).Except(before.Selected.Select(item => item.Epoch)));
        var after = await SnapshotAsync(id);
        await AssertCompleteAsync(id, after);
        foreach (var prior in before.Selected)
            after.Selected.Single(item => item.Epoch == prior.Epoch).Items.Should().Equal(prior.Items);
        foreach (var epoch in receipt.Selectors)
            after.Selected.Single(item => item.Epoch == epoch).Items.Should().Equal(GroupSelectionWorkflow.Items(epoch, current: true));
        foreach (var prior in before.Expansions) after.Expansions.Should().Contain(prior);
        foreach (var prior in before.Items) after.Items.Should().Contain(prior);
        foreach (var prior in before.Occurrences) after.Occurrences.Should().Contain(prior);
        foreach (var prior in before.Memberships) after.Memberships.Should().Contain(prior);
        foreach (var prior in before.Closures) after.Closures.Should().Contain(prior);
        foreach (var prior in before.Steps.Where(step => step.Status == StepStatus.Completed)) after.Steps.Should().Contain(prior);
        foreach (var prior in before.Steps.Where(step => step.Status == StepStatus.Running))
        {
            var current = after.Steps.Single(step => step.Key == prior.Key);
            current.Id.Should().Be(prior.Id);
            current.Revision.Should().Be(prior.Revision);
            current.Attempt.Should().Be(prior.Attempt + 1);
        }
        var completed = before.Steps.Where(step => step.Status == StepStatus.Completed).Select(step => step.Key).ToHashSet();
        receipt.Calls.Should().NotContain(call => completed.Contains(call.StepKey));
        receipt.Calls.Select(call => call.StepKey).Should().OnlyHaveUniqueItems();
        receipt.Calls.Count.Should().Be(after.Items.Count - before.Memberships.Count(item => completed.Contains(item.StepKey)));
        testOutput.WriteLine("SELECTION_RECOVERY_EVIDENCE=" + JsonSerializer.Serialize(new { Mode = mode, CrashExit = 93, RecoveryExit = 0, Before = before, After = after, Receipt = receipt }));
    }

    private WorkflowEngine Engine(GroupSelectionWorkflow workflow, TimeProvider? clock = null) => new(CreateStore(),
        GroupSelectionWorkflow.Registry(workflow), new ZhinuOptions { PollInterval = TimeSpan.FromMilliseconds(10) }, timeProvider: clock);

    private static async Task<Guid> CompleteAsync(WorkflowEngine engine)
    {
        var id = await engine.StartAsync(GroupSelectionWorkflow.Name, "1", "stable-schedule", cancellationToken: Ct);
        await engine.ExecuteAsync(id, Ct);
        (await engine.WaitForCompletionAsync<string>(id, cancellationToken: Ct)).Should().Be("five-epochs-complete");
        return id;
    }

    private async Task<int> WorkerAsync(string runPath, string mode)
    {
        var output = new DirectoryInfo(AppContext.BaseDirectory);
        var probe = Path.Combine(output.Parent!.Parent!.Parent!.Parent!.FullName, "Penghou.Zhinu.Sqlite.ProcessProbe", "bin",
            output.Parent.Name, output.Name, "Penghou.Zhinu.Sqlite.ProcessProbe.dll");
        var start = new ProcessStartInfo { FileName = "dotnet", UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { probe, "--zhinu-group-selection-probe", Path.Combine(root, "zhinu.db"), runPath, mode }) start.ArgumentList.Add(arg);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        using var process = Process.Start(start)!;
        try { await process.WaitForExitAsync(timeout.Token); }
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

    private async Task AssertCompleteAsync(Guid id, Snapshot snapshot)
    {
        snapshot.Status.Should().Be(WorkflowStatus.Completed);
        snapshot.Selected.Should().HaveCount(5);
        snapshot.Expansions.Should().HaveCount(5);
        snapshot.Items.Should().HaveCount(snapshot.Selected.Sum(epoch => epoch.Items.Count));
        snapshot.Occurrences.Should().HaveCount(snapshot.Items.Count + 1);
        snapshot.Items.Select(item => item.Occurrence).Should().OnlyHaveUniqueItems();
        snapshot.Memberships.Should().HaveCount(snapshot.Items.Count);
        snapshot.States.Should().HaveCount(5).And.OnlyContain(state => state.IsComplete);
        snapshot.Steps.Should().OnlyContain(step => step.Status == StepStatus.Completed);
        (await CreateStore().HasIncompleteGroupFanOutAsync(id, Ct)).Should().BeFalse();
        var parent = snapshot.Occurrences.Single(item => item.Key == "root");
        foreach (var item in snapshot.Items)
        {
            var occurrence = snapshot.Occurrences.Single(value => value.Id == item.Occurrence);
            occurrence.Parent.Should().Be(parent.Id);
            occurrence.Key.Should().Be("work");
            var frames = ActivityGroupOccurrence.ReadCoordinate(occurrence.Coordinate);
            frames.Should().ContainSingle().Which.Scope.Should().Be(item.Scope);
            frames[0].Token.Should().Be(item.Key);
            snapshot.Memberships.Should().Contain(member => member.StepKey == item.StepKey && member.GroupOccurrenceId == item.Occurrence);
        }
    }

    private async Task<Snapshot> SnapshotAsync(Guid id)
    {
        var store = CreateStore();
        var steps = (await store.GetStepsAsync(id, Ct)).OrderBy(step => step.StepKey, StringComparer.Ordinal).ToArray();
        var selected = new List<Selected>();
        var states = new List<ActivityGroupFanOutState>();
        for (var epoch = 1; epoch <= 5; epoch++)
        {
            var step = steps.SingleOrDefault(step => step.StepKey == GroupSelectionWorkflow.Selection(epoch) && step.Status == StepStatus.Completed);
            if (step is not null) selected.Add(new Selected(epoch,
                JsonSerializer.Deserialize<SelectionItem[]>(step.OutputJson!, new JsonSerializerOptions(JsonSerializerDefaults.Web))!));
            var state = await store.GetGroupFanOutAsync(id, GroupSelectionWorkflow.Scope(epoch), Ct);
            if (state is not null) states.Add(state);
        }
        var memberships = new List<ActivityGroupStepMembership>();
        foreach (var step in steps)
        {
            var membership = await store.GetStepGroupMembershipAsync(step.Id, Ct);
            if (membership is not null) memberships.Add(membership);
        }
        await using var connection = new SqliteConnection($"Data Source={Path.Combine(root, "zhinu.db")};Pooling=False");
        await connection.OpenAsync(Ct);
        var expansions = await RowsAsync("SELECT expansion_id,fan_out_key,boundary_step_id,boundary_step_key,boundary_revision,plan_digest,canonical_json,item_count,admitted_generation FROM workflow_group_fanout_expansions WHERE workflow_run_id=$id ORDER BY fan_out_key,expansion_id;");
        var closures = await RowsAsync("SELECT expansion_id,closure_digest,current_steps_json,closure_generation,completed_at FROM workflow_group_fanout_closures WHERE workflow_run_id=$id ORDER BY expansion_id,closure_digest;");
        var items = new List<Item>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT e.fan_out_key,i.expansion_id,i.item_key,i.step_key,i.input_hash,i.group_occurrence_id,i.role
                FROM workflow_group_fanout_items i JOIN workflow_group_fanout_expansions e
                ON e.workflow_run_id=i.workflow_run_id AND e.expansion_id=i.expansion_id
                WHERE i.workflow_run_id=$id ORDER BY e.fan_out_key,i.item_key;
                """;
            command.Parameters.AddWithValue("$id", id.ToString("D"));
            await using var reader = await command.ExecuteReaderAsync(Ct);
            while (await reader.ReadAsync(Ct)) items.Add(new Item(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetInt32(6)));
        }
        var occurrences = new Dictionary<string, Occurrence>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            for (var occurrenceId = item.Occurrence; occurrenceId is not null;)
            {
                var value = (await store.GetGroupOccurrenceAsync(id, occurrenceId, Ct))!;
                occurrences.TryAdd(value.GroupOccurrenceId, new Occurrence(value.GroupOccurrenceId, value.GroupKey, value.ParentGroupOccurrenceId, value.ExecutionCoordinate));
                occurrenceId = value.ParentGroupOccurrenceId;
            }
        }
        await using (var count = connection.CreateCommand())
        {
            count.CommandText = "SELECT COUNT(*) FROM workflow_group_occurrences WHERE workflow_run_id=$id;";
            count.Parameters.AddWithValue("$id", id.ToString("D"));
            Convert.ToInt32(await count.ExecuteScalarAsync(Ct)).Should().Be(occurrences.Count);
        }
        return new Snapshot((await store.GetRunAsync(id, Ct))!.Status,
            steps.Select(step => new Step(step.Id, step.StepKey, step.Revision, step.Attempt, step.Status, step.InputJson, step.OutputJson)).ToArray(),
            selected, expansions, items, closures, states, memberships, occurrences.Values.OrderBy(value => value.Id, StringComparer.Ordinal).ToArray());

        async Task<IReadOnlyList<string>> RowsAsync(string sql)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("$id", id.ToString("D"));
            await using var reader = await command.ExecuteReaderAsync(Ct);
            var rows = new List<string>();
            while (await reader.ReadAsync(Ct))
                rows.Add(JsonSerializer.Serialize(Enumerable.Range(0, reader.FieldCount).Select(reader.GetValue).ToArray()));
            return rows;
        }
    }

    private sealed record Snapshot(WorkflowStatus Status, IReadOnlyList<Step> Steps, IReadOnlyList<Selected> Selected,
        IReadOnlyList<string> Expansions, IReadOnlyList<Item> Items, IReadOnlyList<string> Closures,
        IReadOnlyList<ActivityGroupFanOutState> States, IReadOnlyList<ActivityGroupStepMembership> Memberships, IReadOnlyList<Occurrence> Occurrences);
    private sealed record Step(Guid Id, string Key, int Revision, int Attempt, StepStatus Status, string? Input, string? Output);
    private sealed record Selected(int Epoch, IReadOnlyList<SelectionItem> Items);
    private sealed record Item(string Scope, string Expansion, string Key, string StepKey, string InputHash, string Occurrence, int Role);
    private sealed record Occurrence(string Id, string Key, string? Parent, string Coordinate);
}
