using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Penghou.Zhinu.Sqlite.ProcessProbe;

namespace Penghou.Zhinu.Sqlite.Tests;

public sealed class ActivityGroupForkRecoveryTests(ITestOutputHelper testOutput) : WorkflowEngineTestBase
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("manifest", 2)]
    [InlineData("membership", 2)]
    [InlineData("event", 2)]
    [InlineData("committed", 2)]
    [InlineData("running", 2)]
    [InlineData("member-completed", 1)]
    [InlineData("joined", 0)]
    public async Task ProcessLoss_ReconstructsForkLineageWithoutChangingSourceOrCompletedSibling(string mode, int expectedCallbacks)
    {
        var store = CreateStore();
        Guid sourceId, siblingId;
        await using (var setup = Engine(store, new GroupForkWorkflow()))
        {
            sourceId = await setup.StartAsync(GroupForkWorkflow.Name, "1", "input", cancellationToken: Ct);
            await setup.ExecuteAsync(sourceId, Ct);
            siblingId = await setup.ForkAsync(sourceId, "work", cancellationToken: Ct);
            await setup.ExecuteAsync(siblingId, Ct);
            (await setup.WaitForCompletionAsync<string>(siblingId, cancellationToken: Ct)).Should().Be("seed:input:work|plain:input");
        }
        var sourceBefore = await SnapshotAsync(store, sourceId);
        var siblingBefore = await SnapshotAsync(store, siblingId);
        var destinationId = Guid.NewGuid();
        var start = new ProcessStartInfo { FileName = "dotnet", UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { ProcessProbePath(), "--zhinu-group-fork-probe", Path.Combine(root, "zhinu.db"), sourceId.ToString("D"), destinationId.ToString("D"), mode })
            start.ArgumentList.Add(argument);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        using var process = Process.Start(start);
        process.Should().NotBeNull();
        await process!.WaitForExitAsync(timeout.Token);
        process.ExitCode.Should().Be(93);

        // Reopen and inspect before executing any recovery code or callbacks.
        var reopened = CreateStore();
        var before = await SnapshotAsync(reopened, destinationId);
        var atomic = mode is "manifest" or "membership" or "event";
        if (atomic)
        {
            before.Run.Should().BeNull();
            before.Manifest.Should().BeNull();
            before.Steps.Should().BeEmpty();
            before.Memberships.Should().BeEmpty();
            before.Occurrences.Should().BeEmpty();
            before.Events.Should().BeEmpty();
            (await DestinationRowsAsync(destinationId)).Should().Be(0);
        }
        else
        {
            before.Run!.SourceRunId.Should().Be(sourceId);
            before.Manifest.Should().Be(GroupForkWorkflow.Manifest().CanonicalJson);
            before.Occurrences.Should().HaveCount(3);
            before.Steps.Select(step => step.StepKey).Should().Contain(["seed", "plain"]);
            if (mode == "committed")
            {
                before.Run.Status.Should().Be(WorkflowStatus.Pending);
                before.Generations.Should().Be(0, "the worker exited after fork commit and before first-generation binding");
            }
            if (mode == "running")
                before.Steps.Single(step => step.StepKey == "work").Status.Should().Be(StepStatus.Running);
            if (mode is "member-completed" or "joined")
                before.Steps.Single(step => step.StepKey == "work").Status.Should().Be(StepStatus.Completed);
        }
        (JsonSerializer.Serialize(await SnapshotAsync(reopened, sourceId))).Should().Be(JsonSerializer.Serialize(sourceBefore));
        (JsonSerializer.Serialize(await SnapshotAsync(reopened, siblingId))).Should().Be(JsonSerializer.Serialize(siblingBefore));
        await DropTriggerAsync();
        var callbacks = new List<string>();
        await using var engine = Engine(reopened, new GroupForkWorkflow(callback: callbacks.Add));
        if (atomic)
            (await engine.ForkAsync(sourceId, "work", new ForkRunOptions { WorkflowRunId = destinationId }, Ct)).Should().Be(destinationId);
        else
        {
            var duplicate = () => engine.ForkAsync(sourceId, "work", new ForkRunOptions { WorkflowRunId = destinationId }, Ct);
            await duplicate.Should().ThrowAsync<WorkflowStateException>();
        }
        await reopened.RecoverExpiredLeasesAsync(DateTimeOffset.UtcNow.AddMinutes(1), Ct);
        await engine.ExecuteAsync(destinationId, Ct);
        (await engine.WaitForCompletionAsync<string>(destinationId, cancellationToken: Ct)).Should().Be("seed:input:work|plain:input");
        callbacks.Should().HaveCount(expectedCallbacks).And.NotContain("seed").And.NotContain("plain");
        var after = await SnapshotAsync(reopened, destinationId);
        after.Run!.Status.Should().Be(WorkflowStatus.Completed);
        after.Run.SourceRunId.Should().Be(sourceId);
        after.Manifest.Should().Be(GroupForkWorkflow.Manifest().CanonicalJson);
        after.Occurrences.Should().HaveCount(3);
        after.Memberships.Select(item => item.StepKey).Should().Equal("seed", "work");
        foreach (var old in before.Memberships) after.Memberships.Should().Contain(old);
        foreach (var old in before.Steps.Where(step => step.Status == StepStatus.Completed)) after.Steps.Should().Contain(old);
        if (!atomic) after.Occurrences.Should().Equal(before.Occurrences);
        if (mode == "running")
        {
            var prior = before.Steps.Single(step => step.StepKey == "work");
            var current = after.Steps.Single(step => step.StepKey == "work");
            current.Id.Should().Be(prior.Id);
            current.Revision.Should().Be(prior.Revision);
            current.Attempt.Should().Be(prior.Attempt + 1);
        }
        var sourceAfter = await SnapshotAsync(reopened, sourceId);
        var siblingAfter = await SnapshotAsync(reopened, siblingId);
        JsonSerializer.Serialize(sourceAfter).Should().Be(JsonSerializer.Serialize(sourceBefore));
        JsonSerializer.Serialize(siblingAfter).Should().Be(JsonSerializer.Serialize(siblingBefore));
        after.Occurrences.Select(item => item.Id).Intersect(siblingAfter.Occurrences.Select(item => item.Id))
            .Should().BeEmpty();
        testOutput.WriteLine("FORK_RECOVERY_EVIDENCE=" + JsonSerializer.Serialize(new
        {
            Mode = mode,
            ProcessExitCode = process.ExitCode,
            RecoveryCallbacks = callbacks,
            SourceBefore = sourceBefore,
            SourceAfter = sourceAfter,
            CompletedSiblingBefore = siblingBefore,
            CompletedSiblingAfter = siblingAfter,
            DestinationBefore = before,
            DestinationAfter = after
        }));
    }

    private static WorkflowEngine Engine(SqliteWorkflowStore store, GroupForkWorkflow workflow) => new(
        store, GroupForkWorkflow.Registry(workflow), new ZhinuOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(10),
            LeaseDuration = TimeSpan.FromSeconds(2),
            LeaseRenewalInterval = TimeSpan.FromMilliseconds(600)
        });

    private async Task<Snapshot> SnapshotAsync(SqliteWorkflowStore store, Guid id)
    {
        var steps = (await store.GetStepsAsync(id, Ct)).OrderBy(step => step.StepKey, StringComparer.Ordinal).ToArray();
        var memberships = new List<ActivityGroupStepMembership>();
        foreach (var step in steps)
        {
            var membership = await store.GetStepGroupMembershipAsync(step.Id, Ct);
            if (membership is not null) memberships.Add(membership);
        }
        var manifest = GroupForkWorkflow.Manifest();
        var rootOccurrence = ActivityGroupOccurrence.Create(id, manifest, "root", null, "[]");
        var macro = ActivityGroupOccurrence.Create(id, manifest, "macro", rootOccurrence, "[]");
        var leaf = ActivityGroupOccurrence.Create(id, manifest, "leaf", macro, "[]");
        var occurrences = new List<Occurrence>();
        foreach (var reference in new[] { rootOccurrence, macro, leaf })
        {
            var actual = await store.GetGroupOccurrenceAsync(id, reference.GroupOccurrenceId, Ct);
            if (actual is not null) occurrences.Add(new Occurrence(actual.GroupOccurrenceId, actual.GroupKey, actual.ParentGroupOccurrenceId, actual.ExecutionCoordinate));
        }
        await using var connection = await OpenAsync();
        await using var generations = connection.CreateCommand();
        generations.CommandText = "SELECT COUNT(*) FROM workflow_generations WHERE workflow_run_id=$id;";
        generations.Parameters.AddWithValue("$id", id.ToString("D"));
        return new Snapshot(await store.GetRunAsync(id, Ct), (await store.GetGroupManifestAsync(id, Ct))?.CanonicalJson,
            steps.Select(step => new Step(step.Id, step.StepKey, step.Revision, step.Attempt, step.Status, step.OutputJson)).ToArray(),
            memberships, occurrences, (await store.GetEventsAsync(id, 0, 100, Ct)).Select(item => new Event(item.Sequence, item.EventType, item.StepKey, item.DataJson)).ToArray(),
            Convert.ToInt32(await generations.ExecuteScalarAsync(Ct)));
    }

    private async Task<SqliteConnection> OpenAsync()
    {
        var connection = new SqliteConnection($"Data Source={Path.Combine(root, "zhinu.db")};Pooling=False");
        await connection.OpenAsync(Ct);
        return connection;
    }

    private async Task DropTriggerAsync()
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "DROP TRIGGER IF EXISTS zhinu_hard_exit_group_fork;";
        await command.ExecuteNonQueryAsync(Ct);
    }

    private async Task<int> DestinationRowsAsync(Guid id)
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT (SELECT COUNT(*) FROM workflow_runs WHERE id=$id) +
                   (SELECT COUNT(*) FROM workflow_group_manifests WHERE workflow_run_id=$id) +
                   (SELECT COUNT(*) FROM workflow_group_occurrences WHERE workflow_run_id=$id) +
                   (SELECT COUNT(*) FROM workflow_step_group_memberships WHERE workflow_run_id=$id) +
                   (SELECT COUNT(*) FROM workflow_steps WHERE workflow_run_id=$id) +
                   (SELECT COUNT(*) FROM workflow_step_dependencies WHERE run_id=$id) +
                   (SELECT COUNT(*) FROM workflow_events WHERE workflow_run_id=$id) +
                   (SELECT COUNT(*) FROM workflow_generations WHERE workflow_run_id=$id);
            """;
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        return Convert.ToInt32(await command.ExecuteScalarAsync(Ct));
    }

    private static string ProcessProbePath()
    {
        var output = new DirectoryInfo(AppContext.BaseDirectory);
        return Path.Combine(output.Parent!.Parent!.Parent!.Parent!.FullName, "Penghou.Zhinu.Sqlite.ProcessProbe", "bin",
            output.Parent.Name, output.Name, "Penghou.Zhinu.Sqlite.ProcessProbe.dll");
    }

    private sealed record Snapshot(WorkflowRun? Run, string? Manifest, IReadOnlyList<Step> Steps,
        IReadOnlyList<ActivityGroupStepMembership> Memberships, IReadOnlyList<Occurrence> Occurrences, IReadOnlyList<Event> Events, int Generations);
    private sealed record Step(Guid Id, string StepKey, int Revision, int Attempt, StepStatus Status, string? OutputJson);
    private sealed record Occurrence(string Id, string GroupKey, string? Parent, string Coordinate);
    private sealed record Event(long Sequence, string EventType, string? StepKey, string? DataJson);
}
