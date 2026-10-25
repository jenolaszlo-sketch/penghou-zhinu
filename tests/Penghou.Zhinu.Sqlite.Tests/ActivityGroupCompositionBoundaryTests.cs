using System.Text.Json;
using FluentAssertions;
using Microsoft.Data.Sqlite;

namespace Penghou.Zhinu.Sqlite.Tests;

public sealed class ActivityGroupCompositionBoundaryTests : WorkflowEngineTestBase
{
    private const string WorkflowName = "group-composition-boundary";
    private const string FanOutForkMessage = "Forking a run with grouped fan-out evidence is not qualified by this fork profile.";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly ITestOutputHelper testOutput;

    public ActivityGroupCompositionBoundaryTests(ITestOutputHelper testOutput) => this.testOutput = testOutput;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OrdinaryStepAsync_RejectsFanOutGroupBeforeCallbackOrStepClaim(bool hasParent)
    {
        var store = CreateStore();
        var workflow = new OrdinaryStepWorkflow(hasParent);
        await using var engine = new WorkflowEngine(store, Registry(workflow), new ZhinuOptions());
        var runId = await engine.StartAsync(WorkflowName, "1", "payload", cancellationToken: Ct);

        await engine.ExecuteAsync(runId, Ct);
        var failed = () => engine.WaitForCompletionAsync<string>(runId, cancellationToken: Ct);
        var exception = await failed.Should().ThrowAsync<WorkflowExecutionFailedException>();
        exception.Which.Error.Message.Should().Be("A per-item fan-out group declaration can only be consumed by native FanOutAsync.");

        workflow.CallbackCalls.Should().Be(0);
        var steps = await store.GetStepsAsync(runId, Ct);
        steps.Should().ContainSingle().Which.StepKey.Should().Be("select");
        (await ReadRunRowsAsync("workflow_group_occurrences", runId)).Should().BeEmpty();
        (await ReadRunRowsAsync("workflow_step_group_memberships", runId)).Should().BeEmpty();
        (await store.GetGroupFanOutAsync(runId, "ordinary", Ct)).Should().BeNull();
        (await store.GetRunAsync(runId, Ct))!.GroupFanOutUsed.Should().BeFalse();

        testOutput.WriteLine("COMPOSITION_REJECTION_EVIDENCE=" + JsonSerializer.Serialize(new
        {
            boundary = "ordinary-step-fan-out-group",
            hasParent,
            runId,
            exception = exception.Which.Error.Message,
            callbackCalls = workflow.CallbackCalls,
            stepKeys = steps.Select(step => step.StepKey).ToArray(),
            groupFanOutUsed = (await store.GetRunAsync(runId, Ct))!.GroupFanOutUsed
        }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForkAsync_RejectsCompletedGroupedFanOutBeforeAnySourceOrDestinationChange(bool explicitDestinationId)
    {
        var store = CreateStore();
        var workflow = new NativeFanOutWorkflow();
        await using var engine = new WorkflowEngine(store, Registry(workflow), new ZhinuOptions());
        var sourceId = await engine.StartAsync(WorkflowName, "1", "payload", cancellationToken: Ct);
        await engine.ExecuteAsync(sourceId, Ct);
        (await engine.WaitForCompletionAsync<string>(sourceId, cancellationToken: Ct)).Should().Be("A:B");
        (await store.GetRunAsync(sourceId, Ct))!.GroupFanOutUsed.Should().BeTrue();
        (await store.GetGroupFanOutAsync(sourceId, "items", Ct))!.IsComplete.Should().BeTrue();

        var destinationId = explicitDestinationId ? Guid.NewGuid() : (Guid?)null;
        var before = await CaptureSnapshotAsync(store, sourceId);
        var runCountBefore = await CountRunsAsync();
        Func<Task<Guid>> fork = explicitDestinationId
            ? () => engine.ForkAsync(sourceId, "finish", new ForkRunOptions { WorkflowRunId = destinationId }, Ct)
            : () => engine.ForkAsync(sourceId, "finish", cancellationToken: Ct);

        var exception = await fork.Should().ThrowAsync<WorkflowConfigurationException>();
        exception.Which.Message.Should().Be(FanOutForkMessage);

        var after = await CaptureSnapshotAsync(store, sourceId);
        after.Should().Be(before);
        (await CountRunsAsync()).Should().Be(runCountBefore);
        if (destinationId is { } chosenId)
            (await store.GetRunAsync(chosenId, Ct)).Should().BeNull();

        testOutput.WriteLine("COMPOSITION_REJECTION_EVIDENCE=" + JsonSerializer.Serialize(new
        {
            boundary = "grouped-fan-out-fork",
            explicitDestinationId,
            sourceId,
            destinationId,
            exception = exception.Which.Message,
            sourceSnapshotBefore = before,
            sourceSnapshotAfter = after,
            runCountBefore,
            runCountAfter = await CountRunsAsync()
        }));
    }

    private WorkflowRegistry Registry(IWorkflow<string, string> workflow) => new WorkflowRegistry().Register(
        new WorkflowRegistration<string, string>(
            new WorkflowDefinition { Name = WorkflowName, Version = "1" },
            () => workflow,
            Manifest()));

    private static ActivityGroupManifest Manifest() => ActivityGroupManifest.Create([
        new ActivityGroupDefinition("root", ActivityGroupKind.Logical, "Root"),
        new ActivityGroupDefinition("work", ActivityGroupKind.Macro, "Work", "root",
            provenance: new ActivityGroupMacroProvenance("AnyMacro", "pin", "invocation"))
    ]);

    private async Task<string> CaptureSnapshotAsync(SqliteWorkflowStore store, Guid runId)
    {
        var run = (await store.GetRunAsync(runId, Ct))!;
        var steps = await store.GetStepsAsync(runId, Ct);
        var members = new List<object>();
        var occurrences = new Dictionary<string, ActivityGroupOccurrence>(StringComparer.Ordinal);
        foreach (var step in steps)
        {
            var membership = await store.GetStepGroupMembershipAsync(step.Id, Ct);
            members.Add(new { step.Id, Membership = membership });
            if (membership is null || occurrences.ContainsKey(membership.GroupOccurrenceId)) continue;
            var occurrence = await store.GetGroupOccurrenceAsync(runId, membership.GroupOccurrenceId, Ct);
            if (occurrence is not null) occurrences.Add(membership.GroupOccurrenceId, occurrence);
        }

        var root = ActivityGroupOccurrence.Create(runId, Manifest(), "root", null, "[]");
        var rootOccurrence = await store.GetGroupOccurrenceAsync(runId, root.GroupOccurrenceId, Ct);
        if (rootOccurrence is not null) occurrences.TryAdd(rootOccurrence.GroupOccurrenceId, rootOccurrence);
        var persistedGroupRows = new Dictionary<string, object[]>(StringComparer.Ordinal);
        foreach (var table in new[]
        {
            "workflow_group_manifests",
            "workflow_group_occurrences",
            "workflow_step_group_memberships",
            "workflow_group_fanout_expansions",
            "workflow_group_fanout_items",
            "workflow_group_fanout_closures"
        })
            persistedGroupRows.Add(table, await ReadRunRowsAsync(table, runId));
        return JsonSerializer.Serialize(new
        {
            Run = run,
            Steps = steps,
            Memberships = members,
            Occurrences = occurrences,
            Manifest = await store.GetGroupManifestAsync(runId, Ct),
            FanOut = await store.GetGroupFanOutAsync(runId, "items", Ct),
            Events = await store.GetEventsAsync(runId, 0, 100, Ct),
            Dependencies = await ReadRunRowsAsync("workflow_step_dependencies", runId, "run_id"),
            PersistedGroupRows = persistedGroupRows
        });
    }

    private async Task<object[]> ReadRunRowsAsync(string table, Guid runId, string runIdColumn = "workflow_run_id")
    {
        await using var connection = new SqliteConnection($"Data Source={Path.Combine(root, "zhinu.db")};Pooling=False");
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT * FROM {table} WHERE {runIdColumn} = $runId ORDER BY rowid";
        command.Parameters.AddWithValue("$runId", runId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var rows = new List<object>();
        while (await reader.ReadAsync(Ct))
        {
            var row = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (var column = 0; column < reader.FieldCount; column++)
                row.Add(reader.GetName(column), await reader.IsDBNullAsync(column, Ct) ? null : reader.GetValue(column));
            rows.Add(row);
        }
        return rows.ToArray();
    }

    private async Task<int> CountRunsAsync()
    {
        await using var connection = new SqliteConnection($"Data Source={Path.Combine(root, "zhinu.db")};Pooling=False");
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM workflow_runs";
        return Convert.ToInt32(await command.ExecuteScalarAsync(Ct));
    }

    private sealed class OrdinaryStepWorkflow(bool hasParent) : IWorkflow<string, string>
    {
        public int CallbackCalls;

        public async Task<string> RunAsync(WorkflowContext context, string input, CancellationToken ct)
        {
            await context.StepAsync("select", input, (value, _) => Task.FromResult(value), cancellationToken: ct);
            var group = new ActivityGroupFanOutBinding(new ActivityGroupMembership("work"), "select",
                hasParent ? context.GetActivityGroup("root") : null);
            return await context.StepAsync("ordinary", input, (_, _) =>
            {
                Interlocked.Increment(ref CallbackCalls);
                return Task.FromResult("unexpected");
            }, new StepOptions { DependsOn = ["select"], FanOutGroup = group }, ct);
        }
    }

    private sealed class NativeFanOutWorkflow : IWorkflow<string, string>
    {
        public async Task<string> RunAsync(WorkflowContext context, string input, CancellationToken ct)
        {
            var selected = await context.StepAsync("select", input, (_, _) => Task.FromResult(new[] { "a", "b" }), cancellationToken: ct);
            var items = await context.FanOutAsync("items", selected, value => value,
                (value, _, _) => Task.FromResult(value.ToUpperInvariant()),
                new FanOutOptions { MaxDegreeOfParallelism = 1 },
                new StepOptions
                {
                    DependsOn = ["select"],
                    FanOutGroup = new ActivityGroupFanOutBinding(new ActivityGroupMembership("work"), "select", context.GetActivityGroup("root"))
                }, ct);
            return await context.StepAsync("finish", string.Join(':', items), (value, _) => Task.FromResult(value), cancellationToken: ct);
        }
    }
}
