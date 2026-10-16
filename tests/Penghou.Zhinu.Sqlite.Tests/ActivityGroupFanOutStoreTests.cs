using FluentAssertions;
using Microsoft.Data.Sqlite;

namespace Penghou.Zhinu.Sqlite.Tests;

public sealed class ActivityGroupFanOutStoreTests : WorkflowEngineTestBase
{
    [Fact]
    public async Task AdmissionAndClosure_AreDurableAndRunRetentionCascadesAllRows()
    {
        var store = CreateStore();
        var manifest = Manifest();
        var run = Run(manifest);
        await store.CreateGroupedRunAsync(run, manifest, TestContext.Current.CancellationToken);
        var (plan, request) = await PrepareAsync(store, run, manifest, ["a", "b"]);

        var admitted = await store.AdmitGroupFanOutAsync(request, TestContext.Current.CancellationToken);
        admitted.ItemCount.Should().Be(2);
        admitted.IsComplete.Should().BeFalse();
        (await store.GetRunAsync(run.Id, TestContext.Current.CancellationToken))!.GroupFanOutUsed.Should().BeTrue();
        (await store.GetGroupOccurrenceAsync(run.Id, plan.Items[0].Group.Occurrence.GroupOccurrenceId,
            TestContext.Current.CancellationToken)).Should().NotBeNull();

        foreach (var item in plan.Items)
            await CompleteItemAsync(store, run, manifest, admitted, item);
        var completed = await store.CompleteGroupFanOutAsync(request, TestContext.Current.CancellationToken);
        completed.IsComplete.Should().BeTrue();
        (await store.HasIncompleteGroupFanOutAsync(run.Id, TestContext.Current.CancellationToken)).Should().BeFalse();

        await using var connection = new SqliteConnection($"Data Source={Path.Combine(root, "zhinu.db")};Pooling=False");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using (var delete = connection.CreateCommand())
        {
            delete.CommandText = "DELETE FROM workflow_runs WHERE id=$run;";
            delete.Parameters.AddWithValue("$run", run.Id.ToString("D"));
            await delete.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
        await using var count = connection.CreateCommand();
        count.CommandText = """
            SELECT (SELECT COUNT(*) FROM workflow_group_fanout_expansions WHERE workflow_run_id=$run) +
                   (SELECT COUNT(*) FROM workflow_group_fanout_items WHERE workflow_run_id=$run) +
                   (SELECT COUNT(*) FROM workflow_group_fanout_closures WHERE workflow_run_id=$run);
            """;
        count.Parameters.AddWithValue("$run", run.Id.ToString("D"));
        Convert.ToInt32(await count.ExecuteScalarAsync(TestContext.Current.CancellationToken)).Should().Be(0);
    }

    [Fact]
    public async Task AdmissionRejectsInputDriftAndOrdinaryClaimOnReservedItemKey()
    {
        var store = CreateStore();
        var manifest = Manifest();
        var run = Run(manifest);
        await store.CreateGroupedRunAsync(run, manifest, TestContext.Current.CancellationToken);
        var (plan, request) = await PrepareAsync(store, run, manifest, ["a"]);
        var admitted = await store.AdmitGroupFanOutAsync(request, TestContext.Current.CancellationToken);

        var ordinaryClaim = () => store.ClaimStepAsync(new StepClaimRequest
        {
            WorkflowRunId = run.Id,
            StepKey = "items.a",
            InputType = "text",
            InputHash = Hash("a"),
            OutputType = "text",
            OwnerId = "worker",
            Now = DateTimeOffset.UtcNow,
            LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(1),
            LeaseGeneration = 1,
            GroupManifestDigest = manifest.ManifestDigest,
            DependsOn = ["selection"]
        }, TestContext.Current.CancellationToken).AsTask();
        await ordinaryClaim.Should().ThrowAsync<WorkflowStateException>();

        var drifted = CreatePlan(run.Id, manifest, [("a", Hash("changed"))]);
        var readmit = () => store.AdmitGroupFanOutAsync(request with { Plan = drifted },
            TestContext.Current.CancellationToken).AsTask();
        await readmit.Should().ThrowAsync<WorkflowStateException>();
        (await store.GetGroupFanOutAsync(run.Id, "items", TestContext.Current.CancellationToken))!
            .PlanDigest.Should().Be(plan.PlanDigest);
        admitted.IsComplete.Should().BeFalse();
    }

    [Fact]
    public async Task ClosureRejectsPartialItemsAndCannotBeCleared()
    {
        var store = CreateStore();
        var manifest = Manifest();
        var run = Run(manifest);
        await store.CreateGroupedRunAsync(run, manifest, TestContext.Current.CancellationToken);
        var (plan, request) = await PrepareAsync(store, run, manifest, ["a", "b"]);
        var admitted = await store.AdmitGroupFanOutAsync(request, TestContext.Current.CancellationToken);
        await CompleteItemAsync(store, run, manifest, admitted, plan.Items[0]);

        var incomplete = () => store.CompleteGroupFanOutAsync(request, TestContext.Current.CancellationToken).AsTask();
        await incomplete.Should().ThrowAsync<WorkflowStateException>();
        (await store.HasIncompleteGroupFanOutAsync(run.Id, TestContext.Current.CancellationToken)).Should().BeTrue();

        await CompleteItemAsync(store, run, manifest, admitted, plan.Items[1]);
        (await store.CompleteGroupFanOutAsync(request, TestContext.Current.CancellationToken)).IsComplete.Should().BeTrue();
        await using var connection = new SqliteConnection($"Data Source={Path.Combine(root, "zhinu.db")};Pooling=False");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var clear = connection.CreateCommand();
        clear.CommandText = "UPDATE workflow_runs SET group_fanout_used=0 WHERE id=$run;";
        clear.Parameters.AddWithValue("$run", run.Id.ToString("D"));
        var act = () => clear.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<SqliteException>();
    }

    [Fact]
    public async Task Schema8Migration_PreservesGroupedRowsAndAddsFalseMarkerAndFanOutTables()
    {
        var databasePath = Path.Combine(root, "zhinu.db");
        var store = CreateStore();
        var manifest = Manifest();
        var run = Run(manifest) with { AuthoritySubjectBinding = "host:subject" };
        await store.CreateGroupedRunAsync(run, manifest, TestContext.Current.CancellationToken);
        var parent = ActivityGroupOccurrence.Create(run.Id, manifest, "outer", null, "[]");
        var request = Claim(run.Id, manifest, new ActivityGroupStepBinding(new ActivityGroupMembership("outer"), parent));
        var claim = await store.ClaimStepAsync(request, TestContext.Current.CancellationToken);

        await using (var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False"))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var downgrade = connection.CreateCommand();
            downgrade.CommandText = """
                DROP TRIGGER trg_workflow_run_group_fanout_marker_immutable;
                DROP TABLE workflow_group_fanout_closures;
                DROP TABLE workflow_group_fanout_items;
                DROP TABLE workflow_group_fanout_expansions;
                ALTER TABLE workflow_runs DROP COLUMN group_fanout_used;
                UPDATE zhinu_schema SET version=8 WHERE id=1;
                """;
            await downgrade.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var upgraded = new SqliteWorkflowStore(new ZhinuSqliteOptions { DatabasePath = databasePath, Pooling = false });
        await upgraded.InitializeAsync(TestContext.Current.CancellationToken);
        await upgraded.InitializeAsync(TestContext.Current.CancellationToken);
        var restoredRun = await upgraded.GetRunAsync(run.Id, TestContext.Current.CancellationToken);
        restoredRun!.GroupManifestDigest.Should().Be(manifest.ManifestDigest);
        restoredRun.AuthoritySubjectBinding.Should().Be("host:subject");
        restoredRun.GroupFanOutUsed.Should().BeFalse();
        (await upgraded.GetGroupManifestAsync(run.Id, TestContext.Current.CancellationToken))!.CanonicalJson
            .Should().Be(manifest.CanonicalJson);
        (await upgraded.GetGroupOccurrenceAsync(run.Id, parent.GroupOccurrenceId, TestContext.Current.CancellationToken))
            .Should().NotBeNull();
        (await upgraded.GetStepGroupMembershipAsync(claim.Step.Id, TestContext.Current.CancellationToken))
            .Should().NotBeNull();
        var (_, fanOutRequest) = await PrepareAsync(upgraded, run, manifest, ["a"]);
        await upgraded.AdmitGroupFanOutAsync(fanOutRequest, TestContext.Current.CancellationToken);
        await using (var clearMarker = new SqliteConnection($"Data Source={databasePath};Pooling=False"))
        {
            await clearMarker.OpenAsync(TestContext.Current.CancellationToken);
            await using var clear = clearMarker.CreateCommand();
            clear.CommandText = "UPDATE workflow_runs SET group_fanout_used=0 WHERE id=$run;";
            clear.Parameters.AddWithValue("$run", run.Id.ToString("D"));
            var act = () => clear.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            await act.Should().ThrowAsync<SqliteException>();
        }

        await using var verify = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        await verify.OpenAsync(TestContext.Current.CancellationToken);
        await using var version = verify.CreateCommand();
        version.CommandText = "SELECT version FROM zhinu_schema WHERE id=1;";
        Convert.ToInt32(await version.ExecuteScalarAsync(TestContext.Current.CancellationToken))
            .Should().Be(ZhinuSqliteSchema.CurrentVersion);
    }

    private async Task<(ActivityGroupFanOutPlan Plan, ActivityGroupFanOutRequest Request)> PrepareAsync(
        SqliteWorkflowStore store, WorkflowRun run, ActivityGroupManifest manifest, string[] keys)
    {
        var now = DateTimeOffset.UtcNow;
        var boundary = await store.ClaimStepAsync(new StepClaimRequest
        {
            WorkflowRunId = run.Id,
            StepKey = "selection",
            InputType = "selection",
            InputHash = Hash("selection"),
            OutputType = "text",
            OwnerId = "worker",
            Now = now,
            LeaseExpiresAt = now.AddMinutes(5),
            LeaseGeneration = 1,
            GroupManifestDigest = manifest.ManifestDigest
        }, TestContext.Current.CancellationToken);
        await store.CompleteStepAsync(boundary.Step.Id, "worker", "{}", now.AddMilliseconds(1),
            TestContext.Current.CancellationToken);
        var plan = CreatePlan(run.Id, manifest, keys.Select(key => (key, Hash(key))).ToArray());
        return (plan, new ActivityGroupFanOutRequest
        {
            Plan = plan,
            OwnerId = "worker",
            LeaseGeneration = 1,
            Now = now.AddMilliseconds(2)
        });
    }

    private async Task CompleteItemAsync(SqliteWorkflowStore store, WorkflowRun run, ActivityGroupManifest manifest,
        ActivityGroupFanOutState expansion, ActivityGroupFanOutItem item)
    {
        var now = DateTimeOffset.UtcNow;
        var claim = await store.ClaimStepAsync(new StepClaimRequest
        {
            WorkflowRunId = run.Id,
            StepKey = item.StepKey,
            InputType = "text",
            InputHash = item.InputHash,
            OutputType = "text",
            OwnerId = "worker",
            Now = now,
            LeaseExpiresAt = now.AddMinutes(1),
            LeaseGeneration = 1,
            GroupManifestDigest = manifest.ManifestDigest,
            Group = item.Group,
            FanOut = new ActivityGroupFanOutItemClaim
            {
                ExpansionId = expansion.ExpansionId,
                ItemKey = item.ItemKey
            },
            DependsOn = ["selection"]
        }, TestContext.Current.CancellationToken);
        await store.CompleteStepAsync(claim.Step.Id, "worker", "{}", now.AddMilliseconds(1),
            TestContext.Current.CancellationToken);
    }

    private static ActivityGroupFanOutPlan CreatePlan(Guid runId, ActivityGroupManifest manifest,
        IEnumerable<(string Key, string Hash)> items) => ActivityGroupFanOutPlan.Create(runId, manifest, "items",
        new ActivityGroupFanOutBinding(new ActivityGroupMembership("outer"), "selection"),
        ActivityGroupFanOutIdentityKind.Keyed, "text", "text", null, null, "none", ["selection"], items);

    private static StepClaimRequest Claim(Guid runId, ActivityGroupManifest manifest,
        ActivityGroupStepBinding binding) => new()
        {
            WorkflowRunId = runId,
            StepKey = "member",
            OutputType = "string",
            OwnerId = "worker",
            Now = DateTimeOffset.UtcNow,
            LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(1),
            LeaseGeneration = 1,
            GroupManifestDigest = manifest.ManifestDigest,
            Group = binding
        };

    private static WorkflowRun Run(ActivityGroupManifest manifest)
    {
        var now = DateTimeOffset.UtcNow;
        return new WorkflowRun
        {
            Id = Guid.NewGuid(),
            WorkflowName = "grouped-fanout",
            WorkflowVersion = "1",
            Status = WorkflowStatus.Running,
            CreatedAt = now,
            UpdatedAt = now,
            LeaseOwner = "worker",
            LeaseExpiresAt = now.AddHours(1),
            GroupManifestDigest = manifest.ManifestDigest
        };
    }

    private static string Hash(string value) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static ActivityGroupManifest Manifest() => ActivityGroupManifest.Create(
        [new ActivityGroupDefinition("outer", ActivityGroupKind.Logical, "Outer")]);
}
