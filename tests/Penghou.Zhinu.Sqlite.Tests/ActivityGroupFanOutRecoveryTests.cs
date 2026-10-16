using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Data.Sqlite;

namespace Penghou.Zhinu.Sqlite.Tests;

public sealed class ActivityGroupFanOutRecoveryTests(ITestOutputHelper testOutput) : WorkflowEngineTestBase
{
    [Theory]
    [InlineData("admit", 2)]
    [InlineData("partial", 1)]
    [InlineData("close", 0)]
    [InlineData("closed", 0)]
    public async Task ProcessLoss_RecoversImmutableGroupedFanOut(string mode, int expectedRecoveryCalls)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var databasePath = Path.Combine(root, $"group-fanout-{mode}.db");
        var runIdPath = Path.Combine(root, $"group-fanout-{mode}.run");
        var processStart = new ProcessStartInfo
        {
            FileName = "dotnet",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        processStart.ArgumentList.Add(GetProcessProbeAssemblyPath());
        processStart.ArgumentList.Add("--zhinu-group-fanout-probe");
        processStart.ArgumentList.Add(databasePath);
        processStart.ArgumentList.Add(runIdPath);
        processStart.ArgumentList.Add(mode);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        using var process = Process.Start(processStart);
        process.Should().NotBeNull();
        await WaitUntilAsync(() => Task.FromResult(File.Exists(runIdPath)), timeout.Token);
        await process!.WaitForExitAsync(timeout.Token);
        process.ExitCode.Should().Be(93);

        var runId = Guid.Parse(await File.ReadAllTextAsync(runIdPath, timeout.Token));
        var store = CreateRecoveryStore(databasePath);
        var beforeRecovery = await CaptureSnapshotAsync(store, runId, timeout.Token);
        var selectedBefore = await ReadSelectedItemsAsync(databasePath, timeout.Token);
        beforeRecovery.Run.Should().NotBeNull();
        beforeRecovery.Manifest.Should().Be(CreateManifest().CanonicalJson);
        beforeRecovery.Run!.GroupFanOutUsed.Should().Be(mode != "admit");
        beforeRecovery.Steps.Select(step => step.StepKey).Should().Contain("select");
        JsonSerializer.Deserialize<string[]>(beforeRecovery.Steps.Single(step => step.StepKey == "select").OutputJson!)
            .Should().Equal("a", "b");

        if (mode == "admit")
        {
            beforeRecovery.Expansion.Should().BeNull("the first item insert terminated admission inside its transaction");
            beforeRecovery.Memberships.Should().BeEmpty();
            beforeRecovery.Occurrences.Should().OnlyContain(item => item == null);
            selectedBefore.Should().BeEmpty();
        }
        else
        {
            beforeRecovery.Expansion.Should().NotBeNull();
            beforeRecovery.Expansion!.IsComplete.Should().Be(mode == "closed");
            beforeRecovery.Expansion.ItemCount.Should().Be(2);
            selectedBefore.Select(item => item.ItemKey).Should().Equal("a", "b");
            beforeRecovery.Occurrences.Should().OnlyContain(item => item != null);
            beforeRecovery.Occurrences.Should().Equal(ExpectedOccurrences(runId, CreateManifest()));
            if (mode == "partial")
                beforeRecovery.Memberships.Select(item => item.StepKey).Should().Equal("items.a");
            else
                beforeRecovery.Memberships.Select(item => item.StepKey).Should().Equal("items.a", "items.b");
        }

        await DropTriggerAsync(databasePath, TriggerName(mode));
        await store.RecoverExpiredLeasesAsync(DateTimeOffset.UtcNow.AddMinutes(1), timeout.Token);

        var probe = new RecoveryProbe();
        var workflow = new RecoveryFanOutWorkflow(probe);
        await using var engine = CreateEngine(store, workflow, "process-group-fanout-" + mode);
        await engine.ExecuteAsync(runId, timeout.Token);
        var output = await engine.WaitForCompletionAsync<string>(runId, cancellationToken: timeout.Token);
        var afterRecovery = await CaptureSnapshotAsync(store, runId, timeout.Token);
        var selectedAfter = await ReadSelectedItemsAsync(databasePath, timeout.Token);

        output.Should().Be("A|B");
        probe.ItemCalls.Should().Be(expectedRecoveryCalls);
        afterRecovery.Manifest.Should().Be(beforeRecovery.Manifest);
        afterRecovery.Expansion.Should().NotBeNull();
        afterRecovery.Expansion!.IsComplete.Should().BeTrue();
        afterRecovery.Expansion.ItemCount.Should().Be(2);
        selectedAfter.Select(item => item.ItemKey).Should().Equal("a", "b");
        if (selectedBefore.Count > 0) selectedAfter.Should().Equal(selectedBefore);
        if (beforeRecovery.Expansion is not null)
        {
            afterRecovery.Expansion.ExpansionId.Should().Be(beforeRecovery.Expansion.ExpansionId);
            afterRecovery.Expansion.PlanDigest.Should().Be(beforeRecovery.Expansion.PlanDigest);
            afterRecovery.Expansion.BoundaryStepId.Should().Be(beforeRecovery.Expansion.BoundaryStepId);
            afterRecovery.Expansion.BoundaryRevision.Should().Be(beforeRecovery.Expansion.BoundaryRevision);
        }

        var expected = ExpectedOccurrences(runId, CreateManifest());
        afterRecovery.Occurrences.Should().Equal(expected);
        afterRecovery.Memberships.Select(item => item.StepKey).Should().Equal("items.a", "items.b");
        foreach (var oldMembership in beforeRecovery.Memberships)
        {
            afterRecovery.Memberships.Should().Contain(item =>
                item.StepId == oldMembership.StepId &&
                item.StepKey == oldMembership.StepKey &&
                item.StepRevision == oldMembership.StepRevision &&
                item.GroupOccurrenceId == oldMembership.GroupOccurrenceId && item.Role == oldMembership.Role);
        }
        foreach (var oldStep in beforeRecovery.Steps.Where(step => step.Status == StepStatus.Completed))
            afterRecovery.Steps.Should().Contain(oldStep);

        afterRecovery.Steps.Single(step => step.StepKey == "items.a").Status.Should().Be(StepStatus.Completed);
        afterRecovery.Steps.Single(step => step.StepKey == "items.b").Status.Should().Be(StepStatus.Completed);
        testOutput.WriteLine("FANOUT_RECOVERY_EVIDENCE=" + JsonSerializer.Serialize(new
        {
            Mode = mode,
            ProcessExitCode = process.ExitCode,
            RecoveryCallbacks = probe.ItemCalls,
            Before = beforeRecovery,
            SelectedBefore = selectedBefore,
            After = afterRecovery,
            SelectedAfter = selectedAfter
        }));
    }

    private static async Task<IReadOnlyList<SelectedItemSnapshot>> ReadSelectedItemsAsync(string path, CancellationToken ct)
    {
        await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT item_key,step_key,input_hash,group_occurrence_id,role FROM workflow_group_fanout_items ORDER BY item_key;";
        await using var reader = await command.ExecuteReaderAsync(ct);
        var selected = new List<SelectedItemSnapshot>();
        while (await reader.ReadAsync(ct))
            selected.Add(new SelectedItemSnapshot(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt32(4)));
        return selected;
    }

    private async Task<DurableSnapshot> CaptureSnapshotAsync(
        SqliteWorkflowStore store,
        Guid runId,
        CancellationToken cancellationToken)
    {
        var manifest = await store.GetGroupManifestAsync(runId, cancellationToken);
        var steps = await store.GetStepsAsync(runId, cancellationToken);
        var memberships = new List<MembershipSnapshot>();
        foreach (var step in steps.OrderBy(item => item.StepKey, StringComparer.Ordinal))
        {
            var membership = await store.GetStepGroupMembershipAsync(step.Id, cancellationToken);
            if (membership is not null)
                memberships.Add(new MembershipSnapshot(
                    membership.StepId,
                    membership.StepKey,
                    membership.StepRevision,
                    membership.GroupOccurrenceId,
                    membership.Role));
        }

        var groupManifest = CreateManifest();
        var occurrenceReferences = ExpectedOccurrences(runId, groupManifest);
        var occurrences = new List<OccurrenceSnapshot?>();
        foreach (var expected in occurrenceReferences)
        {
            var occurrence = await store.GetGroupOccurrenceAsync(runId, expected.GroupOccurrenceId, cancellationToken);
            occurrences.Add(occurrence is null
                ? null
                : new OccurrenceSnapshot(
                    occurrence.GroupOccurrenceId,
                    occurrence.GroupKey,
                    occurrence.ParentGroupOccurrenceId,
                    occurrence.ExecutionCoordinate));
        }

        var run = await store.GetRunAsync(runId, cancellationToken);
        var expansion = await store.GetGroupFanOutAsync(runId, "items", cancellationToken);
        return new DurableSnapshot(
            run,
            manifest?.CanonicalJson,
            steps.Select(step => new StepSnapshot(step.Id, step.StepKey, step.Revision, step.Status, step.Attempt, step.OutputJson))
                .OrderBy(step => step.StepKey, StringComparer.Ordinal).ToArray(),
            memberships,
            occurrences,
            expansion);
    }

    private static IReadOnlyList<OccurrenceSnapshot> ExpectedOccurrences(Guid runId, ActivityGroupManifest manifest)
    {
        var parent = ActivityGroupOccurrence.Create(runId, manifest, "root", null, "[]");
        return new[]
        {
            parent,
            ActivityGroupOccurrence.Create(runId, manifest, "work", parent,
                ActivityGroupOccurrence.FanOutCoordinate("items", ActivityGroupFanOutIdentityKind.Keyed, "a")),
            ActivityGroupOccurrence.Create(runId, manifest, "work", parent,
                ActivityGroupOccurrence.FanOutCoordinate("items", ActivityGroupFanOutIdentityKind.Keyed, "b"))
        }.Select(item => new OccurrenceSnapshot(
            item.GroupOccurrenceId,
            item.GroupKey,
            item.ParentGroupOccurrenceId,
            item.ExecutionCoordinate)).ToArray();
    }

    private static SqliteWorkflowStore CreateRecoveryStore(string databasePath) => new(new ZhinuSqliteOptions
    {
        DatabasePath = databasePath,
        BusyTimeout = TimeSpan.FromSeconds(5),
        Pooling = false
    });

    private static WorkflowEngine CreateEngine(
        SqliteWorkflowStore store,
        RecoveryFanOutWorkflow workflow,
        string name) => new(
            store,
            new WorkflowRegistry().Register(
                new WorkflowRegistration<string, string>(
                    new WorkflowDefinition { Name = name, Version = "1" },
                    () => workflow,
                    CreateManifest())),
            new ZhinuOptions
            {
                PollInterval = TimeSpan.FromMilliseconds(10),
                LeaseDuration = TimeSpan.FromSeconds(2),
                LeaseRenewalInterval = TimeSpan.FromMilliseconds(600)
            });

    private static async Task DropTriggerAsync(string databasePath, string triggerName)
    {
        if (triggerName.Length == 0) return;
        await using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"DROP TRIGGER IF EXISTS {triggerName};";
        await command.ExecuteNonQueryAsync();
    }

    private static string TriggerName(string mode) => mode switch
    {
        "admit" => "zhinu_hard_exit_fanout_admit",
        "partial" => "zhinu_hard_exit_fanout_partial",
        "close" => "zhinu_hard_exit_fanout_close",
        _ => string.Empty
    };

    private static ActivityGroupManifest CreateManifest() => ActivityGroupManifest.Create(new[]
    {
        new ActivityGroupDefinition("root", ActivityGroupKind.Logical, "Root", childOrdinal: 0),
        new ActivityGroupDefinition("work", ActivityGroupKind.Logical, "Work", "root")
    });

    private static string GetProcessProbeAssemblyPath()
    {
        var testOutput = new DirectoryInfo(AppContext.BaseDirectory);
        var targetFramework = testOutput.Name;
        var configuration = testOutput.Parent?.Name ?? "Debug";
        var testsRoot = testOutput.Parent?.Parent?.Parent?.Parent?.FullName ??
            throw new InvalidOperationException("Could not resolve the test output root.");
        return Path.Combine(
            testsRoot,
            "Penghou.Zhinu.Sqlite.ProcessProbe",
            "bin",
            configuration,
            targetFramework,
            "Penghou.Zhinu.Sqlite.ProcessProbe.dll");
    }

    private sealed record DurableSnapshot(
        WorkflowRun? Run,
        string? Manifest,
        IReadOnlyList<StepSnapshot> Steps,
        IReadOnlyList<MembershipSnapshot> Memberships,
        IReadOnlyList<OccurrenceSnapshot?> Occurrences,
        ActivityGroupFanOutState? Expansion);

    private sealed record StepSnapshot(Guid Id, string StepKey, int Revision, StepStatus Status, int Attempt, string? OutputJson);
    private sealed record SelectedItemSnapshot(string ItemKey, string StepKey, string InputHash, string OccurrenceId, int Role);
    private sealed record MembershipSnapshot(Guid StepId, string StepKey, int StepRevision, string GroupOccurrenceId, ActivityGroupMemberRole Role);
    private sealed record OccurrenceSnapshot(string GroupOccurrenceId, string GroupKey, string? ParentGroupOccurrenceId, string ExecutionCoordinate);

    private sealed class RecoveryProbe
    {
        private int itemCalls;
        public int ItemCalls => Volatile.Read(ref itemCalls);
        public void CountItem() => Interlocked.Increment(ref itemCalls);
    }

    private sealed class RecoveryFanOutWorkflow(RecoveryProbe probe) : IWorkflow<string, string>
    {
        public async Task<string> RunAsync(WorkflowContext context, string input, CancellationToken cancellationToken)
        {
            var selected = await context.StepAsync(
                "select",
                input,
                (value, _, _) => Task.FromResult(value.Split(',', StringSplitOptions.RemoveEmptyEntries)),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            var parent = context.GetActivityGroup("root");
            var result = await context.FanOutAsync(
                "items",
                selected,
                item => item,
                (item, _, _) =>
                {
                    probe.CountItem();
                    return Task.FromResult(item.ToUpperInvariant());
                },
                new FanOutOptions { MaxDegreeOfParallelism = 1 },
                new StepOptions
                {
                    DependsOn = ["select"],
                    FanOutGroup = new ActivityGroupFanOutBinding(new ActivityGroupMembership("work"), "select", parent)
                },
                cancellationToken).ConfigureAwait(false);
            return string.Join('|', result);
        }
    }
}
