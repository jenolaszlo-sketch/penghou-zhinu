using FluentAssertions;
using Microsoft.Data.Sqlite;

namespace Penghou.Zhinu.Sqlite.Tests;

public sealed class ActivityGroupSqliteTests : WorkflowEngineTestBase
{
    [Fact]
    public async Task GroupedRun_StoresCanonicalManifestAndClaimedMembershipAtomically()
    {
        var store = CreateStore();
        var manifest = CreateManifest();
        var run = CreateRun(manifest);
        await store.CreateGroupedRunAsync(run, manifest, TestContext.Current.CancellationToken);

        var restored = await store.GetGroupManifestAsync(run.Id, TestContext.Current.CancellationToken);
        restored.Should().NotBeNull();
        restored!.CanonicalJson.Should().Be(manifest.CanonicalJson);
        restored.ManifestDigest.Should().Be(manifest.ManifestDigest);
        var runSnapshot = await store.GetRunAsync(run.Id, TestContext.Current.CancellationToken);
        runSnapshot!.GroupManifestDigest.Should().Be(manifest.ManifestDigest);

        var parent = ActivityGroupOccurrence.Create(run.Id, manifest, "outer", null, "[]");
        var child = ActivityGroupOccurrence.Create(run.Id, manifest, "inner", parent, "[]");
        var request = Claim(run.Id, manifest, new ActivityGroupStepBinding(
            new ActivityGroupMembership("inner", ActivityGroupMemberRole.Work), child));
        var claim = await store.ClaimStepAsync(request, TestContext.Current.CancellationToken);
        var membership = await store.GetStepGroupMembershipAsync(claim.Step.Id, TestContext.Current.CancellationToken);

        membership.Should().NotBeNull();
        membership!.WorkflowRunId.Should().Be(run.Id);
        membership.StepRevision.Should().Be(claim.Step.Revision);
        membership.GroupOccurrenceId.Should().Be(child.GroupOccurrenceId);
        membership.Role.Should().Be(ActivityGroupMemberRole.Work);
        var restoredChild = await store.GetGroupOccurrenceAsync(run.Id, child.GroupOccurrenceId,
            TestContext.Current.CancellationToken);
        restoredChild.Should().NotBeNull();
        restoredChild!.ParentGroupOccurrenceId.Should().Be(parent.GroupOccurrenceId);
        restoredChild.ExecutionCoordinate.Should().Be("[]");
    }

    [Fact]
    public async Task Claim_RejectsChangedPinAndChangedMembershipOnSameRevision()
    {
        var store = CreateStore();
        var manifest = CreateManifest();
        var run = CreateRun(manifest);
        await store.CreateGroupedRunAsync(run, manifest, TestContext.Current.CancellationToken);
        var occurrence = ActivityGroupOccurrence.Create(run.Id, manifest, "outer", null, "[]");
        var binding = new ActivityGroupStepBinding(new ActivityGroupMembership("outer"), occurrence);
        var request = Claim(run.Id, manifest, binding);
        var first = await store.ClaimStepAsync(request, TestContext.Current.CancellationToken);

        var wrongPin = () => store.ClaimStepAsync(request with { GroupManifestDigest = null },
            TestContext.Current.CancellationToken).AsTask();
        await wrongPin.Should().ThrowAsync<WorkflowStateException>();
        var changedRole = request with
        {
            Group = new ActivityGroupStepBinding(
                new ActivityGroupMembership("outer", ActivityGroupMemberRole.Control), occurrence)
        };
        var wrongMembership = () => store.ClaimStepAsync(changedRole, TestContext.Current.CancellationToken).AsTask();
        await wrongMembership.Should().ThrowAsync<WorkflowStateException>();

        var persisted = await store.GetStepGroupMembershipAsync(first.Step.Id, TestContext.Current.CancellationToken);
        persisted!.Role.Should().Be(ActivityGroupMemberRole.Work);

        var ungroupedRequest = Claim(run.Id, manifest, group: null) with { StepKey = "plain" };
        var plain = await store.ClaimStepAsync(ungroupedRequest, TestContext.Current.CancellationToken);
        var lateBinding = ungroupedRequest with
        {
            Group = new ActivityGroupStepBinding(new ActivityGroupMembership("outer"), occurrence)
        };
        var addAfterAdmission = () => store.ClaimStepAsync(lateBinding, TestContext.Current.CancellationToken).AsTask();
        await addAfterAdmission.Should().ThrowAsync<WorkflowStateException>();
        (await store.GetStepGroupMembershipAsync(plain.Step.Id, TestContext.Current.CancellationToken))
            .Should().BeNull();
    }

    [Fact]
    public async Task Restart_ClonesMembershipToNewStepRevision()
    {
        var store = CreateStore();
        var manifest = CreateManifest();
        var run = CreateRun(manifest);
        await store.CreateGroupedRunAsync(run, manifest, TestContext.Current.CancellationToken);
        var occurrence = ActivityGroupOccurrence.Create(run.Id, manifest, "outer", null, "[]");
        var request = Claim(run.Id, manifest, new ActivityGroupStepBinding(
            new ActivityGroupMembership("outer", ActivityGroupMemberRole.Control), occurrence));
        var first = await store.ClaimStepAsync(request, TestContext.Current.CancellationToken);

        await store.RestartStepAsync(run.Id, "work", StepRestartMode.StepOnly,
            actor: null, reason: null, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);

        var steps = await store.GetStepsAsync(run.Id, TestContext.Current.CancellationToken);
        var current = steps.Single(step => step.StepKey == "work");
        current.Revision.Should().Be(first.Step.Revision + 1);
        var membership = await store.GetStepGroupMembershipAsync(current.Id, TestContext.Current.CancellationToken);
        membership.Should().NotBeNull();
        membership!.GroupOccurrenceId.Should().Be(occurrence.GroupOccurrenceId);
        membership.Role.Should().Be(ActivityGroupMemberRole.Control);
    }

    [Fact]
    public async Task ExpiredLeaseReclaim_ReusesExactOccurrenceAndRevisionMembership()
    {
        var store = CreateStore();
        var manifest = CreateManifest();
        var run = CreateRun(manifest);
        await store.CreateGroupedRunAsync(run, manifest, TestContext.Current.CancellationToken);
        var occurrence = ActivityGroupOccurrence.Create(run.Id, manifest, "outer", null, "[]");
        var binding = new ActivityGroupStepBinding(new ActivityGroupMembership("outer"), occurrence);
        var started = DateTimeOffset.UtcNow;
        var request = Claim(run.Id, manifest, binding) with
        {
            Now = started,
            LeaseExpiresAt = started.AddSeconds(1)
        };
        var first = await store.ClaimStepAsync(request, TestContext.Current.CancellationToken);
        var second = await store.ClaimStepAsync(request with
        {
            Now = started.AddSeconds(2),
            LeaseExpiresAt = started.AddSeconds(3)
        }, TestContext.Current.CancellationToken);

        second.Disposition.Should().Be(StepClaimDisposition.Acquired);
        second.Step.Id.Should().Be(first.Step.Id);
        second.Step.Revision.Should().Be(first.Step.Revision);
        second.Step.Attempt.Should().Be(2);
        (await store.GetGroupOccurrenceAsync(run.Id, occurrence.GroupOccurrenceId,
            TestContext.Current.CancellationToken)).Should().NotBeNull();
        var membership = await store.GetStepGroupMembershipAsync(second.Step.Id, TestContext.Current.CancellationToken);
        membership!.GroupOccurrenceId.Should().Be(occurrence.GroupOccurrenceId);
        membership.StepRevision.Should().Be(second.Step.Revision);
    }

    [Fact]
    public async Task StaleRunGenerationClaim_LeavesNoOccurrenceOrMembership()
    {
        var store = CreateStore();
        var manifest = CreateManifest();
        var run = CreateRun(manifest);
        await store.CreateGroupedRunAsync(run, manifest, TestContext.Current.CancellationToken);
        await using (var connection = new SqliteConnection($"Data Source={Path.Combine(root, "zhinu.db")};Pooling=False"))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var update = connection.CreateCommand();
            update.CommandText = "UPDATE workflow_runs SET lease_generation = 2 WHERE id = $run;";
            update.Parameters.AddWithValue("$run", run.Id.ToString("D"));
            await update.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
        var occurrence = ActivityGroupOccurrence.Create(run.Id, manifest, "outer", null, "[]");
        var request = Claim(run.Id, manifest, new ActivityGroupStepBinding(
            new ActivityGroupMembership("outer"), occurrence));

        var stale = () => store.ClaimStepAsync(request, TestContext.Current.CancellationToken).AsTask();
        await stale.Should().ThrowAsync<LeaseLostException>();
        (await store.GetGroupOccurrenceAsync(run.Id, occurrence.GroupOccurrenceId,
            TestContext.Current.CancellationToken)).Should().BeNull();
        (await store.GetStepsAsync(run.Id, TestContext.Current.CancellationToken)).Should().BeEmpty();
    }

    [Fact]
    public async Task MembershipInsertFailure_RollsBackStepOccurrenceAndEvent()
    {
        var store = CreateStore();
        var manifest = CreateManifest();
        var run = CreateRun(manifest);
        await store.CreateGroupedRunAsync(run, manifest, TestContext.Current.CancellationToken);
        await using (var connection = new SqliteConnection($"Data Source={Path.Combine(root, "zhinu.db")};Pooling=False"))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var trigger = connection.CreateCommand();
            trigger.CommandText = """
                CREATE TRIGGER reject_group_membership
                BEFORE INSERT ON workflow_step_group_memberships
                BEGIN SELECT RAISE(ABORT, 'injected membership failure'); END;
                """;
            await trigger.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
        var parent = ActivityGroupOccurrence.Create(run.Id, manifest, "outer", null, "[]");
        var child = ActivityGroupOccurrence.Create(run.Id, manifest, "inner", parent, "[]");
        var request = Claim(run.Id, manifest, new ActivityGroupStepBinding(
            new ActivityGroupMembership("inner"), child));

        var claim = () => store.ClaimStepAsync(request, TestContext.Current.CancellationToken).AsTask();
        (await claim.Should().ThrowAsync<WorkflowPersistenceException>())
            .Which.InnerException.Should().BeOfType<SqliteException>();
        (await store.GetStepsAsync(run.Id, TestContext.Current.CancellationToken)).Should().BeEmpty();
        (await store.GetGroupOccurrenceAsync(run.Id, parent.GroupOccurrenceId,
            TestContext.Current.CancellationToken)).Should().BeNull();
        (await store.GetGroupOccurrenceAsync(run.Id, child.GroupOccurrenceId,
            TestContext.Current.CancellationToken)).Should().BeNull();
        var events = await store.GetEventsAsync(run.Id, 0, 100, TestContext.Current.CancellationToken);
        events.Select(item => item.EventType).Should().ContainSingle().Which.Should().Be(WorkflowEventTypes.WorkflowStarted);
    }

    [Fact]
    public async Task MembershipForeignKey_BindsStepIdToSameRunAndExactRevision()
    {
        var store = CreateStore();
        var manifest = CreateManifest();
        var localRun = CreateRun(manifest);
        var foreignRun = CreateRun(manifest);
        await store.CreateGroupedRunAsync(localRun, manifest, TestContext.Current.CancellationToken);
        await store.CreateGroupedRunAsync(foreignRun, manifest, TestContext.Current.CancellationToken);
        var localOccurrence = ActivityGroupOccurrence.Create(localRun.Id, manifest, "outer", null, "[]");
        var foreignOccurrence = ActivityGroupOccurrence.Create(foreignRun.Id, manifest, "outer", null, "[]");
        var local = await store.ClaimStepAsync(Claim(localRun.Id, manifest,
            new ActivityGroupStepBinding(new ActivityGroupMembership("outer"), localOccurrence)) with
        { StepKey = "same-key" }, TestContext.Current.CancellationToken);
        var foreign = await store.ClaimStepAsync(Claim(foreignRun.Id, manifest,
            new ActivityGroupStepBinding(new ActivityGroupMembership("outer"), foreignOccurrence)) with
        { StepKey = "same-key" }, TestContext.Current.CancellationToken);

        await using var connection = new SqliteConnection($"Data Source={Path.Combine(root, "zhinu.db")};Pooling=False");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using (var delete = connection.CreateCommand())
        {
            delete.CommandText = "DELETE FROM workflow_step_group_memberships WHERE step_id = $step;";
            delete.Parameters.AddWithValue("$step", local.Step.Id.ToString("D"));
            await delete.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
        await using var forged = connection.CreateCommand();
        forged.CommandText = """
            INSERT INTO workflow_step_group_memberships
                (workflow_run_id, step_id, step_key, step_revision, group_occurrence_id, role)
            VALUES ($run, $foreignStep, 'same-key', 1, $occurrence, 0);
            """;
        forged.Parameters.AddWithValue("$run", localRun.Id.ToString("D"));
        forged.Parameters.AddWithValue("$foreignStep", foreign.Step.Id.ToString("D"));
        forged.Parameters.AddWithValue("$occurrence", localOccurrence.GroupOccurrenceId);

        var insert = () => forged.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        await insert.Should().ThrowAsync<SqliteException>();
    }

    [Fact]
    public async Task RunRetentionCascade_RemovesNestedOccurrencesAndMemberships()
    {
        var store = CreateStore();
        var manifest = CreateManifest();
        var run = CreateRun(manifest);
        await store.CreateGroupedRunAsync(run, manifest, TestContext.Current.CancellationToken);
        var parent = ActivityGroupOccurrence.Create(run.Id, manifest, "outer", null, "[]");
        var child = ActivityGroupOccurrence.Create(run.Id, manifest, "inner", parent, "[]");
        var claim = await store.ClaimStepAsync(Claim(run.Id, manifest,
            new ActivityGroupStepBinding(new ActivityGroupMembership("inner"), child)),
            TestContext.Current.CancellationToken);

        await using var connection = new SqliteConnection($"Data Source={Path.Combine(root, "zhinu.db")};Pooling=False");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using (var deleteRun = connection.CreateCommand())
        {
            deleteRun.CommandText = "DELETE FROM workflow_runs WHERE id = $run;";
            deleteRun.Parameters.AddWithValue("$run", run.Id.ToString("D"));
            await deleteRun.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
        await using var count = connection.CreateCommand();
        count.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM workflow_group_manifests WHERE workflow_run_id = $run) +
                (SELECT COUNT(*) FROM workflow_group_occurrences WHERE workflow_run_id = $run) +
                (SELECT COUNT(*) FROM workflow_step_group_memberships WHERE workflow_run_id = $run);
            """;
        count.Parameters.AddWithValue("$run", run.Id.ToString("D"));
        Convert.ToInt32(await count.ExecuteScalarAsync(TestContext.Current.CancellationToken)).Should().Be(0);
        (await store.GetStepGroupMembershipAsync(claim.Step.Id, TestContext.Current.CancellationToken)).Should().BeNull();
    }

    [Fact]
    public async Task OrdinaryCreateRun_RejectsManifestPinWithoutAtomicManifestCapability()
    {
        var store = CreateStore();
        var manifest = CreateManifest();
        var run = CreateRun(manifest);
        var create = () => store.CreateRunAsync(run, TestContext.Current.CancellationToken).AsTask();

        await create.Should().ThrowAsync<WorkflowStateException>();
        (await store.GetRunAsync(run.Id, TestContext.Current.CancellationToken)).Should().BeNull();
    }

    [Fact]
    public async Task InitializeAsync_V7Database_AddsGroupStorageAndAdvancesOnce()
    {
        var databasePath = Path.Combine(root, "zhinu.db");
        Directory.CreateDirectory(root);
        var store = CreateStore();
        await store.InitializeAsync(TestContext.Current.CancellationToken);
        var legacyRun = new WorkflowRun
        {
            Id = Guid.NewGuid(),
            WorkflowName = "legacy-v7",
            WorkflowVersion = "1",
            Status = WorkflowStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            AuthoritySubjectBinding = "host:subject"
        };
        await store.CreateRunAsync(legacyRun, TestContext.Current.CancellationToken);
        var legacyStep = await store.ClaimStepAsync(new StepClaimRequest
        {
            WorkflowRunId = legacyRun.Id,
            StepKey = "legacy-step",
            OutputType = "string",
            OwnerId = "worker",
            Now = DateTimeOffset.UtcNow,
            LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(1),
            LeaseGeneration = 1
        }, TestContext.Current.CancellationToken);
        await using (var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False"))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var downgrade = connection.CreateCommand();
            downgrade.CommandText = """
                DROP TRIGGER trg_workflow_run_group_pin_immutable;
                DROP INDEX ux_workflow_steps_group_membership_fk;
                DROP TABLE workflow_step_group_memberships;
                DROP TABLE workflow_group_occurrences;
                DROP TABLE workflow_group_manifests;
                ALTER TABLE workflow_runs DROP COLUMN group_manifest_digest;
                UPDATE zhinu_schema SET version = 7 WHERE id = 1;
                """;
            await downgrade.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        await new SqliteWorkflowStore(new ZhinuSqliteOptions { DatabasePath = databasePath, Pooling = false })
            .InitializeAsync(TestContext.Current.CancellationToken);
        await new SqliteWorkflowStore(new ZhinuSqliteOptions { DatabasePath = databasePath, Pooling = false })
            .InitializeAsync(TestContext.Current.CancellationToken);

        var preserved = await new SqliteWorkflowStore(new ZhinuSqliteOptions { DatabasePath = databasePath, Pooling = false })
            .GetRunAsync(legacyRun.Id, TestContext.Current.CancellationToken);
        preserved!.AuthoritySubjectBinding.Should().Be("host:subject");
        preserved.GroupManifestDigest.Should().BeNull();
        var preservedStep = await new SqliteWorkflowStore(new ZhinuSqliteOptions { DatabasePath = databasePath, Pooling = false })
            .GetStepGroupMembershipAsync(legacyStep.Step.Id, TestContext.Current.CancellationToken);
        preservedStep.Should().BeNull();

        await using (var verify = new SqliteConnection($"Data Source={databasePath};Pooling=False"))
        {
            await verify.OpenAsync(TestContext.Current.CancellationToken);
            await using var check = verify.CreateCommand();
            check.CommandText = """
                SELECT version,
                       (SELECT COUNT(*) FROM pragma_table_info('workflow_runs') WHERE name='group_manifest_digest'),
                       (SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='workflow_step_group_memberships'),
                       (SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='ux_workflow_steps_group_membership_fk')
                FROM zhinu_schema WHERE id=1;
                """;
            await using var reader = await check.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            (await reader.ReadAsync(TestContext.Current.CancellationToken)).Should().BeTrue();
            reader.GetInt32(0).Should().Be(8);
            reader.GetInt32(1).Should().Be(1);
            reader.GetInt32(2).Should().Be(1);
            reader.GetInt32(3).Should().Be(1);
        }

        var groupedManifest = CreateManifest();
        var groupedRun = CreateRun(groupedManifest);
        var upgraded = new SqliteWorkflowStore(new ZhinuSqliteOptions { DatabasePath = databasePath, Pooling = false });
        await upgraded.CreateGroupedRunAsync(groupedRun, groupedManifest, TestContext.Current.CancellationToken);
        var occurrence = ActivityGroupOccurrence.Create(groupedRun.Id, groupedManifest, "outer", null, "[]");
        var claim = await upgraded.ClaimStepAsync(Claim(groupedRun.Id, groupedManifest,
            new ActivityGroupStepBinding(new ActivityGroupMembership("outer"), occurrence)),
            TestContext.Current.CancellationToken);
        (await upgraded.GetStepGroupMembershipAsync(claim.Step.Id, TestContext.Current.CancellationToken))
            .Should().NotBeNull();
    }

    private static ActivityGroupManifest CreateManifest() => ActivityGroupManifest.Create(
    [
        new ActivityGroupDefinition("outer", ActivityGroupKind.Logical, "Outer"),
        new ActivityGroupDefinition("inner", ActivityGroupKind.Macro, "Inner", "outer", 0,
            new ActivityGroupMacroProvenance("macro", "fingerprint", "invocation"))
    ]);

    private static WorkflowRun CreateRun(ActivityGroupManifest manifest)
    {
        var now = DateTimeOffset.UtcNow;
        return new WorkflowRun
        {
            Id = Guid.NewGuid(),
            WorkflowName = "group-store-test",
            WorkflowVersion = "1",
            Status = WorkflowStatus.Pending,
            CreatedAt = now,
            UpdatedAt = now,
            GroupManifestDigest = manifest.ManifestDigest
        };
    }

    private static StepClaimRequest Claim(
        Guid runId,
        ActivityGroupManifest manifest,
        ActivityGroupStepBinding? group) => new()
        {
            WorkflowRunId = runId,
            StepKey = "work",
            OutputType = "string",
            OwnerId = "worker",
            Now = DateTimeOffset.UtcNow,
            LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(1),
            LeaseGeneration = 1,
            GroupManifestDigest = manifest.ManifestDigest,
            Group = group
        };
}
