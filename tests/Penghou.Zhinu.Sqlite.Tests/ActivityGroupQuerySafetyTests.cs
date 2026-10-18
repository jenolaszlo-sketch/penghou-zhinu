using System.Text.Json;
using FluentAssertions;
using Microsoft.Data.Sqlite;

namespace Penghou.Zhinu.Sqlite.Tests;

public sealed class ActivityGroupQuerySafetyTests : WorkflowEngineTestBase
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SnapshotExcludesPayloadErrorMetadataTraceAndAuthority_AndDoesNotBudgetIrrelevantPayload()
    {
        var store = CreateStore();
        var runId = await CreateGroupedRunAsync(store, "query-safe-payload", RootAndWorkManifest(), "initial-input");
        const string sentinel = "PRIVATE-SNAPSHOT-SENTINEL-94c01c";
        var large = new string('x', 1_100_000) + sentinel;
        await using (var connection = await OpenAsync())
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE workflow_runs SET input_json=$payload,output_json=$payload,error_json=$payload,
                    metadata_json=$payload,trace_id=$trace,authorization_provider_id=$provider,
                    authorization_binding_id=$binding,authority_subject_binding=$subject WHERE id=$run;
                UPDATE workflow_steps SET input_json=$payload,output_json=$payload,error_json=$payload,
                    authorization_declaration_hash=$declaration WHERE workflow_run_id=$run;
                """;
            command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(large));
            command.Parameters.AddWithValue("$trace", sentinel + "-trace");
            command.Parameters.AddWithValue("$provider", sentinel + "-provider");
            command.Parameters.AddWithValue("$binding", sentinel + "-binding");
            command.Parameters.AddWithValue("$subject", sentinel + "-subject");
            command.Parameters.AddWithValue("$declaration", sentinel + "-declaration");
            command.Parameters.AddWithValue("$run", runId.ToString("D"));
            await command.ExecuteNonQueryAsync(Ct);
        }

        var result = await store.ReadGroupSnapshotAsync(runId, new WorkflowGroupReadLimits
        {
            MaximumRows = 100,
            MaximumBytes = 16_384
        }, Ct);

        result.Status.Should().Be(WorkflowGroupReadStatus.Success, result.Reason);
        result.Value.Should().NotBeNull();
        result.Value!.EstimatedBytes.Should().BeLessThan(16_384);
        JsonSerializer.Serialize(result.Value).Should().NotContain(sentinel);
        JsonSerializer.Serialize(result.Value).Should().NotContain(new string('x', 100));
    }

    [Fact]
    public async Task SnapshotRejectsProjectedManifestThatExceedsByteBudget()
    {
        var manifest = ActivityGroupManifest.Create([
            new ActivityGroupDefinition("root", ActivityGroupKind.Logical, new string('p', 1024)),
            new ActivityGroupDefinition("work", ActivityGroupKind.Macro, "Work", "root",
                provenance: new ActivityGroupMacroProvenance("Work", "fingerprint", "query-projected-limit"))
        ]);
        var store = CreateStore();
        var runId = await CreateGroupedRunAsync(store, "query-projected-limit", manifest, "input");

        var result = await store.ReadGroupSnapshotAsync(runId, new WorkflowGroupReadLimits
        {
            MaximumRows = 100,
            MaximumBytes = 1024
        }, Ct);

        result.Status.Should().Be(WorkflowGroupReadStatus.LimitExceeded);
        result.Value.Should().BeNull("the store must not return a partial snapshot after the byte preflight fails");
    }

    [Fact]
    public async Task SnapshotReportsCorruptOccurrenceLineageWithoutReturningPartialData()
    {
        var store = CreateStore();
        var runId = await CreateGroupedRunAsync(store, "query-corrupt-lineage", RootAndWorkManifest(), "input");
        await using (var connection = await OpenAsync())
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                DROP TRIGGER trg_workflow_group_occurrence_immutable;
                UPDATE workflow_group_occurrences SET execution_coordinate='["corrupt"]' WHERE workflow_run_id=$run;
                """;
            command.Parameters.AddWithValue("$run", runId.ToString("D"));
            await command.ExecuteNonQueryAsync(Ct);
        }

        var result = await store.ReadGroupSnapshotAsync(runId, new WorkflowGroupReadLimits(), Ct);

        result.Status.Should().Be(WorkflowGroupReadStatus.InconsistentData);
        result.Value.Should().BeNull();
    }

    [Fact]
    public async Task SnapshotHotLookupsUseRunAndStepIndexes()
    {
        var store = CreateStore();
        _ = await CreateGroupedRunAsync(store, "query-index-plan", RootAndWorkManifest(), "input");
        await using var connection = await OpenAsync();
        var runId = (await store.GetRunsAsync(new RunQuery { WorkflowName = "query-index-plan" }, Ct)).Single().Id;

        var childPlan = await ExplainAsync(connection,
            $"SELECT id FROM workflow_runs WHERE parent_run_id='{runId:D}';");
        var occurrencePlan = await ExplainAsync(connection,
            $"SELECT group_occurrence_id FROM workflow_group_occurrences WHERE workflow_run_id='{runId:D}' ORDER BY group_occurrence_id;");
        var stepPlan = await ExplainAsync(connection,
            $"SELECT id,step_key,revision FROM workflow_steps WHERE workflow_run_id='{runId:D}' ORDER BY created_at,step_key,revision;");

        childPlan.Should().Contain(line => line.Contains("ix_workflow_runs_parent", StringComparison.OrdinalIgnoreCase));
        occurrencePlan.Should().Contain(line => line.Contains("INDEX", StringComparison.OrdinalIgnoreCase)
            && line.Contains("workflow_group_occurrences", StringComparison.OrdinalIgnoreCase));
        stepPlan.Should().Contain(line => line.Contains("ix_workflow_steps_run", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task FanOutSnapshotAcceptsExplicitParentWithStaticAncestor_AndKeepsAllOccurrences()
    {
        var store = CreateStore();
        var manifest = ActivityGroupManifest.Create([
            new ActivityGroupDefinition("root", ActivityGroupKind.Logical, "Root"),
            new ActivityGroupDefinition("middle", ActivityGroupKind.Macro, "Middle", "root",
                provenance: new ActivityGroupMacroProvenance("Middle", "middle-fingerprint", "middle-invocation")),
            new ActivityGroupDefinition("work", ActivityGroupKind.Macro, "Work", "middle",
                provenance: new ActivityGroupMacroProvenance("Work", "work-fingerprint", "work-invocation"))
        ]);
        var registration = new WorkflowRegistration<string, string>(
            new WorkflowDefinition { Name = "query-fanout-static-ancestor", Version = "1" },
            () => new FanOutSnapshotWorkflow(nested: true), manifest);
        await using (var engine = new WorkflowEngine(store, new WorkflowRegistry().Register(registration)))
        {
            var runId = await engine.StartAsync("query-fanout-static-ancestor", "1", "payload", cancellationToken: Ct);
            await engine.ExecuteAsync(runId, Ct);
            (await engine.WaitForCompletionAsync<string>(runId, cancellationToken: Ct)).Should().Be("A:B");

            var result = await store.ReadGroupSnapshotAsync(runId, new WorkflowGroupReadLimits(), Ct);
            result.Status.Should().Be(WorkflowGroupReadStatus.Success, result.Reason);
            result.Value!.Occurrences.Should().HaveCount(4);
            var middle = result.Value.Occurrences.Single(item => item.GroupKey == "middle");
            middle.ParentOccurrenceId.Should().NotBeNull();
            middle.ExecutionCoordinate.Should().Be("[]");
            var items = result.Value.Expansions.Should().ContainSingle().Which.Items;
            items.Should().HaveCount(2);
            result.Value.Occurrences.Where(item => item.GroupKey == "work")
                .Should().OnlyContain(item => item.ParentOccurrenceId == middle.OccurrenceId);
        }
    }

    [Fact]
    public async Task FanOutSnapshotAcceptsRootDefinitionWithNoParentOccurrence()
    {
        var store = CreateStore();
        var manifest = ActivityGroupManifest.Create([
            new ActivityGroupDefinition("root", ActivityGroupKind.Logical, "Root")
        ]);
        var registration = new WorkflowRegistration<string, string>(
            new WorkflowDefinition { Name = "query-fanout-root-parent", Version = "1" },
            () => new FanOutSnapshotWorkflow(nested: false), manifest);
        await using var engine = new WorkflowEngine(store, new WorkflowRegistry().Register(registration));
        var runId = await engine.StartAsync("query-fanout-root-parent", "1", "payload", cancellationToken: Ct);
        await engine.ExecuteAsync(runId, Ct);
        (await engine.WaitForCompletionAsync<string>(runId, cancellationToken: Ct)).Should().Be("A:B");

        var result = await store.ReadGroupSnapshotAsync(runId, new WorkflowGroupReadLimits(), Ct);
        result.Status.Should().Be(WorkflowGroupReadStatus.Success, result.Reason);
        result.Value!.Occurrences.Should().HaveCount(2);
        result.Value.Occurrences.Should().OnlyContain(item => item.GroupKey == "root" && item.ParentOccurrenceId == null);
    }

    [Fact]
    public async Task PartialFanOutSnapshotShowsEveryAdmittedItemButOnlyTheClaimedMember()
    {
        var store = CreateStore();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var manifest = ActivityGroupManifest.Create([
            new ActivityGroupDefinition("root", ActivityGroupKind.Logical, "Root"),
            new ActivityGroupDefinition("middle", ActivityGroupKind.Macro, "Middle", "root",
                provenance: new ActivityGroupMacroProvenance("Middle", "middle-fingerprint", "middle-invocation")),
            new ActivityGroupDefinition("work", ActivityGroupKind.Macro, "Work", "middle",
                provenance: new ActivityGroupMacroProvenance("Work", "work-fingerprint", "work-invocation"))
        ]);
        var workflow = new FanOutSnapshotWorkflow(nested: true, entered, release);
        var registration = new WorkflowRegistration<string, string>(
            new WorkflowDefinition { Name = "query-fanout-partial", Version = "1" }, () => workflow, manifest);
        await using var engine = new WorkflowEngine(store, new WorkflowRegistry().Register(registration));
        var runId = await engine.StartAsync("query-fanout-partial", "1", "payload", cancellationToken: Ct);
        var execution = engine.ExecuteAsync(runId, Ct);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(20), Ct);
            using var reader = new WorkflowGroupReader(CreateStore());
            var snapshot = (await reader.GetRunGroupSnapshotAsync(runId, Ct)).Value!;
            var expansionPage = await reader.QueryGroupExpansionsAsync(runId, snapshot.SnapshotToken,
                pageSize: 10, cancellationToken: Ct);
            expansionPage.Status.Should().Be(WorkflowGroupReadStatus.Success);
            var expansion = expansionPage.Value!.Items.Should().ContainSingle().Which;
            expansion.Items.Should().HaveCount(2, "both selections are durably admitted before item claims begin");
            expansion.IsComplete.Should().BeFalse();
            snapshot.OccurrenceCount.Should().Be(4);

            var first = expansion.Items.Single(item => item.ItemKey == "a");
            var second = expansion.Items.Single(item => item.ItemKey == "b");
            var firstMembers = await reader.QueryGroupMembersAsync(runId, snapshot.SnapshotToken,
                first.OccurrenceId, pageSize: 10, cancellationToken: Ct);
            var secondMembers = await reader.QueryGroupMembersAsync(runId, snapshot.SnapshotToken,
                second.OccurrenceId, pageSize: 10, cancellationToken: Ct);
            firstMembers.Value!.Items.Should().ContainSingle(member => member.Step.StepKey == "items.a" && member.Step.Status == StepStatus.Running);
            secondMembers.Value!.Items.Should().BeEmpty("the second selected occurrence exists before its step is claimed");

            release.TrySetResult();
            await execution;
            (await engine.WaitForCompletionAsync<string>(runId, cancellationToken: Ct)).Should().Be("A:B");
            var completedSnapshot = (await reader.GetRunGroupSnapshotAsync(runId, Ct)).Value!;
            var completed = (await reader.QueryGroupExpansionsAsync(runId, completedSnapshot.SnapshotToken,
                cancellationToken: Ct)).Value!.Items.Single();
            completed.IsComplete.Should().BeTrue();
            completed.Items.Select(item => item.OccurrenceId).Should().BeEquivalentTo(expansion.Items.Select(item => item.OccurrenceId));
        }
        finally
        {
            release.TrySetResult();
            try { await execution; }
            catch when (Ct.IsCancellationRequested) { }
        }
    }

    [Fact]
    public async Task MissingRunHasNoCreationStampAndReturnsRunNotFound()
    {
        var store = CreateStore();
        var missing = Guid.NewGuid();

        (await store.GetGroupSnapshotRunStampAsync(missing, Ct)).Should().BeNull();
        var result = await store.ReadGroupSnapshotAsync(missing, new WorkflowGroupReadLimits(), Ct);
        result.Status.Should().Be(WorkflowGroupReadStatus.RunNotFound);
        result.Value.Should().BeNull();
    }

    private async Task<Guid> CreateGroupedRunAsync(SqliteWorkflowStore store, string name,
        ActivityGroupManifest manifest, string input)
    {
        var registration = new WorkflowRegistration<string, string>(
            new WorkflowDefinition { Name = name, Version = "1" }, () => new GroupedQueryWorkflow(), manifest);
        await using var engine = new WorkflowEngine(store, new WorkflowRegistry().Register(registration));
        var runId = await engine.StartAsync(name, "1", input, cancellationToken: Ct);
        await engine.ExecuteAsync(runId, Ct);
        (await engine.WaitForCompletionAsync<string>(runId, cancellationToken: Ct)).Should().Be(input);
        return runId;
    }

    private static ActivityGroupManifest RootAndWorkManifest() => ActivityGroupManifest.Create([
        new ActivityGroupDefinition("root", ActivityGroupKind.Logical, "Root"),
        new ActivityGroupDefinition("work", ActivityGroupKind.Macro, "Work", "root",
            provenance: new ActivityGroupMacroProvenance("Work", "fingerprint", "query-safety"))
    ]);

    private async Task<SqliteConnection> OpenAsync()
    {
        var connection = new SqliteConnection($"Data Source={Path.Combine(root, "zhinu.db")};Pooling=False");
        await connection.OpenAsync(Ct);
        return connection;
    }

    private static async Task<IReadOnlyList<string>> ExplainAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + sql;
        var lines = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct)) lines.Add(reader.GetString(3));
        return lines;
    }

    private sealed class GroupedQueryWorkflow : IWorkflow<string, string>
    {
        public Task<string> RunAsync(WorkflowContext context, string input, CancellationToken cancellationToken)
        {
            var root = context.GetActivityGroup("root");
            var work = context.GetActivityGroup("work", root);
            return context.StepAsync("member", input, (value, _) => Task.FromResult(value),
                new StepOptions { Group = new ActivityGroupStepBinding(new ActivityGroupMembership("work"), work) },
                cancellationToken);
        }
    }

    private sealed class FanOutSnapshotWorkflow(bool nested, TaskCompletionSource? entered = null,
        TaskCompletionSource? release = null) : IWorkflow<string, string>
    {
        public async Task<string> RunAsync(WorkflowContext context, string input, CancellationToken cancellationToken)
        {
            var selected = await context.StepAsync("select", input,
                (_, _) => Task.FromResult(new[] { "a", "b" }), cancellationToken: cancellationToken);
            var parent = nested ? context.GetActivityGroup("middle", context.GetActivityGroup("root")) : null;
            var membership = new ActivityGroupMembership(nested ? "work" : "root");
            var result = await context.FanOutAsync("items", selected, value => value,
                async (value, _, ct) =>
                {
                    if (value == "a" && entered is not null && release is not null)
                    {
                        entered.TrySetResult();
                        await release.Task.WaitAsync(ct);
                    }
                    return value.ToUpperInvariant();
                },
                new FanOutOptions { MaxDegreeOfParallelism = 1 }, new StepOptions
                {
                    DependsOn = ["select"],
                    FanOutGroup = new ActivityGroupFanOutBinding(membership, "select", parent)
                }, cancellationToken);
            return string.Join(':', result);
        }
    }
}
