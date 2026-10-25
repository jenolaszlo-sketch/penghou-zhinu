using FluentAssertions;
using Microsoft.Data.Sqlite;

namespace Penghou.Zhinu.Sqlite.Tests;

public sealed class ActivityGroupForkStoreTests : WorkflowEngineTestBase
{
    [Fact]
    public async Task GroupedFork_RebindsNestedMembershipAndPreservesCopiedStepEvidence()
    {
        var store = CreateStore();
        var sourceManifest = CreateManifest("Source", childOrdinal: 1);
        var targetManifest = CreateManifest("Destination", childOrdinal: 9);
        var source = CreateRun(sourceManifest);
        await store.CreateGroupedRunAsync(source, sourceManifest, TestContext.Current.CancellationToken);

        var parent = ActivityGroupOccurrence.Create(source.Id, sourceManifest, "outer", null, "[]");
        var child = ActivityGroupOccurrence.Create(source.Id, sourceManifest, "inner", parent,
            "[{\"loop\":\"batch\",\"iteration\":2}]");
        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-2);
        var request = new StepClaimRequest
        {
            WorkflowRunId = source.Id,
            StepKey = "work",
            ImplementationKey = "worker-v1",
            InputJson = "{\"value\":7}",
            InputType = "input-v1",
            InputHash = "input-hash",
            OutputType = "output-v1",
            OwnerId = "worker",
            Now = startedAt,
            LeaseExpiresAt = startedAt.AddMinutes(1),
            LeaseGeneration = 1,
            GroupManifestDigest = sourceManifest.ManifestDigest,
            AuthorizationDeclarationHash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            Group = new ActivityGroupStepBinding(
                new ActivityGroupMembership("inner", ActivityGroupMemberRole.Control), child)
        };
        var claimed = await store.ClaimStepAsync(request, TestContext.Current.CancellationToken);
        await store.CompleteStepAsync(claimed.Step.Id, "worker", "{\"result\":11}",
            startedAt.AddSeconds(12), TestContext.Current.CancellationToken);
        var sourceStep = (await store.GetStepsAsync(source.Id, TestContext.Current.CancellationToken)).Single();
        var sourceMembership = await store.GetStepGroupMembershipAsync(sourceStep.Id, TestContext.Current.CancellationToken);
        var sourceOccurrence = await store.GetGroupOccurrenceAsync(source.Id, child.GroupOccurrenceId,
            TestContext.Current.CancellationToken);

        var destination = CreateDestination(source, targetManifest);
        var plan = await store.ForkGroupedRunAsync(source.Id, destination, targetManifest, "new-step",
            StepRestartMode.Dependents, actor: "operator", reason: "version update",
            TestContext.Current.CancellationToken);

        plan.StepsToReuse.Should().ContainSingle().Which.Should().Be("work");
        var destinationRun = await store.GetRunAsync(destination.Id, TestContext.Current.CancellationToken);
        destinationRun!.GroupManifestDigest.Should().Be(targetManifest.ManifestDigest);
        (await store.GetGroupManifestAsync(destination.Id, TestContext.Current.CancellationToken))!
            .CanonicalJson.Should().Be(targetManifest.CanonicalJson);
        var copied = (await store.GetStepsAsync(destination.Id, TestContext.Current.CancellationToken)).Single();
        copied.Id.Should().NotBe(sourceStep.Id);
        copied.WorkflowRunId.Should().Be(destination.Id);
        copied.Revision.Should().Be(1);
        copied.LeaseGeneration.Should().Be(destination.LeaseGeneration);
        copied.Attempt.Should().Be(sourceStep.Attempt);
        copied.StepKey.Should().Be(sourceStep.StepKey);
        copied.ImplementationKey.Should().Be(sourceStep.ImplementationKey);
        copied.InputJson.Should().Be(sourceStep.InputJson);
        copied.InputType.Should().Be(sourceStep.InputType);
        copied.InputHash.Should().Be(sourceStep.InputHash);
        copied.OutputJson.Should().Be(sourceStep.OutputJson);
        copied.OutputType.Should().Be(sourceStep.OutputType);
        copied.CreatedAt.Should().Be(sourceStep.CreatedAt);
        copied.StartedAt.Should().Be(sourceStep.StartedAt);
        copied.CompletedAt.Should().Be(sourceStep.CompletedAt);
        copied.AuthorizationDeclarationHash.Should().Be(sourceStep.AuthorizationDeclarationHash);

        var membership = await store.GetStepGroupMembershipAsync(copied.Id, TestContext.Current.CancellationToken);
        membership.Should().NotBeNull();
        membership!.WorkflowRunId.Should().Be(destination.Id);
        membership.StepId.Should().Be(copied.Id);
        membership.StepKey.Should().Be(copied.StepKey);
        membership.StepRevision.Should().Be(copied.Revision);
        membership.Role.Should().Be(ActivityGroupMemberRole.Control);
        membership.GroupOccurrenceId.Should().NotBe(sourceMembership!.GroupOccurrenceId);
        var rebound = await store.GetGroupOccurrenceAsync(destination.Id, membership.GroupOccurrenceId,
            TestContext.Current.CancellationToken);
        rebound.Should().NotBeNull();
        rebound!.WorkflowRunId.Should().Be(destination.Id);
        rebound.GroupKey.Should().Be("inner");
        rebound.ExecutionCoordinate.Should().Be(sourceOccurrence!.ExecutionCoordinate);
        rebound.ParentGroupOccurrenceId.Should().NotBe(sourceOccurrence.ParentGroupOccurrenceId);
        var reboundParent = await store.GetGroupOccurrenceAsync(destination.Id, rebound.ParentGroupOccurrenceId!,
            TestContext.Current.CancellationToken);
        reboundParent.Should().NotBeNull();
        reboundParent!.GroupKey.Should().Be("outer");
        reboundParent.ParentGroupOccurrenceId.Should().BeNull();

        (await store.GetStepsAsync(source.Id, TestContext.Current.CancellationToken)).Single().Should().Be(sourceStep);
        (await store.GetStepGroupMembershipAsync(sourceStep.Id, TestContext.Current.CancellationToken))
            .Should().Be(sourceMembership);
        (await store.GetGroupOccurrenceAsync(source.Id, child.GroupOccurrenceId, TestContext.Current.CancellationToken))
            .Should().BeEquivalentTo(sourceOccurrence);
    }

    [Fact]
    public async Task LegacyForkEntryPoint_RejectsGroupedMetadataWithoutCreatingDestination()
    {
        var store = CreateStore();
        var manifest = CreateManifest("Source", childOrdinal: 1);
        var source = CreateRun(manifest);
        await store.CreateGroupedRunAsync(source, manifest, TestContext.Current.CancellationToken);
        var destination = CreateDestination(source, manifest);

        var fork = () => store.ForkRunAsync(source.Id, destination, "new-step", StepRestartMode.Dependents,
            actor: null, reason: null, TestContext.Current.CancellationToken).AsTask();

        await fork.Should().ThrowAsync<WorkflowStateException>();
        (await store.GetRunAsync(destination.Id, TestContext.Current.CancellationToken)).Should().BeNull();
        (await store.GetGroupManifestAsync(destination.Id, TestContext.Current.CancellationToken)).Should().BeNull();
    }

    [Theory]
    [InlineData("parent")]
    [InlineData("kind")]
    [InlineData("provenance")]
    public async Task GroupedFork_IncompatibleTargetDefinitionRollsBackDestination(string mismatch)
    {
        var store = CreateStore();
        var sourceManifest = CreateManifest("Source", childOrdinal: 1);
        var source = CreateRun(sourceManifest);
        await store.CreateGroupedRunAsync(source, sourceManifest, TestContext.Current.CancellationToken);
        var parent = ActivityGroupOccurrence.Create(source.Id, sourceManifest, "outer", null, "[]");
        var child = ActivityGroupOccurrence.Create(source.Id, sourceManifest, "inner", parent, "[]");
        var claim = await store.ClaimStepAsync(new StepClaimRequest
        {
            WorkflowRunId = source.Id,
            StepKey = "work",
            OutputType = "output-v1",
            OwnerId = "worker",
            Now = DateTimeOffset.UtcNow,
            LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(1),
            GroupManifestDigest = sourceManifest.ManifestDigest,
            Group = new ActivityGroupStepBinding(new ActivityGroupMembership("inner"), child)
        }, TestContext.Current.CancellationToken);
        await store.CompleteStepAsync(claim.Step.Id, "worker", "done", DateTimeOffset.UtcNow,
            TestContext.Current.CancellationToken);
        var targetManifest = mismatch switch
        {
            "parent" => ActivityGroupManifest.Create([
                new ActivityGroupDefinition("outer", ActivityGroupKind.Logical, "Outer"),
                new ActivityGroupDefinition("inner", ActivityGroupKind.Macro, "Inner", childOrdinal: 1, provenance:
                    new ActivityGroupMacroProvenance("macro", "fingerprint", "invocation"))]),
            "kind" => ActivityGroupManifest.Create([
                new ActivityGroupDefinition("outer", ActivityGroupKind.Logical, "Outer"),
                new ActivityGroupDefinition("inner", ActivityGroupKind.Logical, "Inner", "outer")]),
            "provenance" => ActivityGroupManifest.Create([
                new ActivityGroupDefinition("outer", ActivityGroupKind.Logical, "Outer"),
                new ActivityGroupDefinition("inner", ActivityGroupKind.Macro, "Inner", "outer", provenance:
                    new ActivityGroupMacroProvenance("macro", "different-fingerprint", "invocation"))]),
            _ => throw new ArgumentOutOfRangeException(nameof(mismatch))
        };
        var destination = CreateDestination(source, targetManifest);

        var fork = () => store.ForkGroupedRunAsync(source.Id, destination, targetManifest, "new-step",
            StepRestartMode.Dependents, null, null, TestContext.Current.CancellationToken).AsTask();

        await fork.Should().ThrowAsync<WorkflowStateException>();
        (await store.GetRunAsync(destination.Id, TestContext.Current.CancellationToken)).Should().BeNull();
        (await store.GetGroupManifestAsync(destination.Id, TestContext.Current.CancellationToken)).Should().BeNull();
        (await store.GetStepsAsync(destination.Id, TestContext.Current.CancellationToken)).Should().BeEmpty();
        (await store.GetEventsAsync(destination.Id, 0, 100, TestContext.Current.CancellationToken)).Should().BeEmpty();

    }

    [Fact]
    public async Task GroupedFork_WrongDestinationPinRollsBackDestination()
    {
        var store = CreateStore();
        var sourceManifest = CreateManifest("Source", childOrdinal: 1);
        var targetManifest = CreateManifest("Target", childOrdinal: 2);
        var source = CreateRun(sourceManifest);
        await store.CreateGroupedRunAsync(source, sourceManifest, TestContext.Current.CancellationToken);
        var occurrence = ActivityGroupOccurrence.Create(source.Id, sourceManifest, "outer", null, "[]");
        var claim = await store.ClaimStepAsync(new StepClaimRequest
        {
            WorkflowRunId = source.Id,
            StepKey = "work",
            OutputType = "output-v1",
            OwnerId = "worker",
            Now = DateTimeOffset.UtcNow,
            LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(1),
            GroupManifestDigest = sourceManifest.ManifestDigest,
            Group = new ActivityGroupStepBinding(new ActivityGroupMembership("outer"), occurrence)
        }, TestContext.Current.CancellationToken);
        await store.CompleteStepAsync(claim.Step.Id, "worker", "done", DateTimeOffset.UtcNow,
            TestContext.Current.CancellationToken);
        var destination = CreateDestination(source, sourceManifest);

        var fork = () => store.ForkGroupedRunAsync(source.Id, destination, targetManifest, "new-step",
            StepRestartMode.Dependents, null, null, TestContext.Current.CancellationToken).AsTask();

        await fork.Should().ThrowAsync<WorkflowStateException>();
        (await store.GetRunAsync(destination.Id, TestContext.Current.CancellationToken)).Should().BeNull();
        (await store.GetGroupManifestAsync(destination.Id, TestContext.Current.CancellationToken)).Should().BeNull();
        (await store.GetStepsAsync(destination.Id, TestContext.Current.CancellationToken)).Should().BeEmpty();
        (await store.GetEventsAsync(destination.Id, 0, 100, TestContext.Current.CancellationToken)).Should().BeEmpty();

        var changedAuthority = CreateDestination(source, sourceManifest) with
        {
            AuthoritySubjectBinding = "different-subject"
        };
        var rejectAuthorityChange = () => store.ForkGroupedRunAsync(source.Id, changedAuthority, sourceManifest,
            "new-step", StepRestartMode.Dependents, null, null, TestContext.Current.CancellationToken).AsTask();
        await rejectAuthorityChange.Should().ThrowAsync<WorkflowStateException>();
        (await store.GetRunAsync(changedAuthority.Id, TestContext.Current.CancellationToken)).Should().BeNull();
    }

    [Fact]
    public async Task GroupedFork_CorruptSourceOccurrenceRollsBackDestination()
    {
        var store = CreateStore();
        var manifest = CreateManifest("Source", childOrdinal: 1);
        var source = CreateRun(manifest);
        await store.CreateGroupedRunAsync(source, manifest, TestContext.Current.CancellationToken);
        var occurrence = ActivityGroupOccurrence.Create(source.Id, manifest, "outer", null, "[]");
        var claim = await store.ClaimStepAsync(new StepClaimRequest
        {
            WorkflowRunId = source.Id,
            StepKey = "work",
            OutputType = "output-v1",
            OwnerId = "worker",
            Now = DateTimeOffset.UtcNow,
            LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(1),
            GroupManifestDigest = manifest.ManifestDigest,
            Group = new ActivityGroupStepBinding(new ActivityGroupMembership("outer"), occurrence)
        }, TestContext.Current.CancellationToken);
        await store.CompleteStepAsync(claim.Step.Id, "worker", "done", DateTimeOffset.UtcNow,
            TestContext.Current.CancellationToken);
        await using (var connection = new SqliteConnection($"Data Source={Path.Combine(root, "zhinu.db")};Pooling=False"))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var corrupt = connection.CreateCommand();
            corrupt.CommandText = """
                DROP TRIGGER trg_workflow_group_occurrence_immutable;
                UPDATE workflow_group_occurrences
                SET execution_coordinate = 'not-json'
                WHERE workflow_run_id = $run AND group_occurrence_id = $occurrence;
                CREATE TRIGGER trg_workflow_group_occurrence_immutable
                BEFORE UPDATE ON workflow_group_occurrences
                BEGIN SELECT RAISE(ABORT, 'workflow group occurrences are immutable'); END;
                """;
            corrupt.Parameters.AddWithValue("$run", source.Id.ToString("D"));
            corrupt.Parameters.AddWithValue("$occurrence", occurrence.GroupOccurrenceId);
            await corrupt.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
        var destination = CreateDestination(source, manifest);

        var fork = () => store.ForkGroupedRunAsync(source.Id, destination, manifest, "new-step",
            StepRestartMode.Dependents, null, null, TestContext.Current.CancellationToken).AsTask();

        await fork.Should().ThrowAsync<WorkflowConfigurationException>();
        (await store.GetRunAsync(destination.Id, TestContext.Current.CancellationToken)).Should().BeNull();
        (await store.GetGroupManifestAsync(destination.Id, TestContext.Current.CancellationToken)).Should().BeNull();
        (await store.GetStepsAsync(destination.Id, TestContext.Current.CancellationToken)).Should().BeEmpty();
        (await store.GetEventsAsync(destination.Id, 0, 100, TestContext.Current.CancellationToken)).Should().BeEmpty();
    }

    [Fact]
    public async Task GroupedFork_RejectsMissingTargetForReusedMembershipButAllowsNoMembership()
    {
        var store = CreateStore();
        var manifest = CreateManifest("Source", childOrdinal: 1);
        var source = CreateRun(manifest);
        await store.CreateGroupedRunAsync(source, manifest, TestContext.Current.CancellationToken);
        var occurrence = ActivityGroupOccurrence.Create(source.Id, manifest, "outer", null, "[]");
        var claim = await store.ClaimStepAsync(new StepClaimRequest
        {
            WorkflowRunId = source.Id,
            StepKey = "grouped",
            OutputType = "output-v1",
            OwnerId = "worker",
            Now = DateTimeOffset.UtcNow,
            LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(1),
            GroupManifestDigest = manifest.ManifestDigest,
            Group = new ActivityGroupStepBinding(new ActivityGroupMembership("outer"), occurrence)
        }, TestContext.Current.CancellationToken);
        await store.CompleteStepAsync(claim.Step.Id, "worker", "done", DateTimeOffset.UtcNow,
            TestContext.Current.CancellationToken);
        var missingTarget = CreateDestination(source, targetManifest: null);

        var reject = () => store.ForkGroupedRunAsync(source.Id, missingTarget, null, "new-step",
            StepRestartMode.Dependents, null, null, TestContext.Current.CancellationToken).AsTask();
        await reject.Should().ThrowAsync<WorkflowStateException>();
        (await store.GetRunAsync(missingTarget.Id, TestContext.Current.CancellationToken)).Should().BeNull();

        var ungroupedSource = CreateRun(manifest);
        await store.CreateGroupedRunAsync(ungroupedSource, manifest, TestContext.Current.CancellationToken);
        var plain = await store.ClaimStepAsync(new StepClaimRequest
        {
            WorkflowRunId = ungroupedSource.Id,
            StepKey = "plain",
            OutputType = "output-v1",
            OwnerId = "worker",
            Now = DateTimeOffset.UtcNow,
            LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(1),
            GroupManifestDigest = manifest.ManifestDigest
        }, TestContext.Current.CancellationToken);
        await store.CompleteStepAsync(plain.Step.Id, "worker", "plain-result", DateTimeOffset.UtcNow,
            TestContext.Current.CancellationToken);
        var noTarget = CreateDestination(ungroupedSource, targetManifest: null);
        await store.ForkGroupedRunAsync(ungroupedSource.Id, noTarget, null, "new-step",
            StepRestartMode.Dependents, null, null, TestContext.Current.CancellationToken);
        (await store.GetRunAsync(noTarget.Id, TestContext.Current.CancellationToken))!.GroupManifestDigest
            .Should().BeNull();
        (await store.GetGroupManifestAsync(noTarget.Id, TestContext.Current.CancellationToken)).Should().BeNull();
        (await store.GetStepGroupMembershipAsync(
            (await store.GetStepsAsync(noTarget.Id, TestContext.Current.CancellationToken)).Single().Id,
            TestContext.Current.CancellationToken)).Should().BeNull();

        var fullyUngroupedSource = CreateRun(manifest: null);
        await store.CreateRunAsync(fullyUngroupedSource, TestContext.Current.CancellationToken);
        var fullyUngroupedDestination = CreateDestination(fullyUngroupedSource, targetManifest: null);
        await store.ForkGroupedRunAsync(fullyUngroupedSource.Id, fullyUngroupedDestination, null, "new-step",
            StepRestartMode.Dependents, null, null, TestContext.Current.CancellationToken);
        (await store.GetRunAsync(fullyUngroupedDestination.Id, TestContext.Current.CancellationToken))!
            .GroupManifestDigest.Should().BeNull();
        (await store.GetGroupManifestAsync(fullyUngroupedDestination.Id, TestContext.Current.CancellationToken))
            .Should().BeNull();
    }

    [Fact]
    public async Task GroupedFork_RejectsFanOutEvidenceAndExistingDestinationWithoutChangingSource()
    {
        var store = CreateStore();
        var manifest = CreateManifest("Source", childOrdinal: 1);
        var source = CreateRun(manifest);
        await store.CreateGroupedRunAsync(source, manifest, TestContext.Current.CancellationToken);
        var destination = CreateDestination(source, manifest);
        var before = await store.GetRunAsync(source.Id, TestContext.Current.CancellationToken);

        var existingDestination = CreateDestination(source, manifest);
        await store.CreateGroupedRunAsync(existingDestination, manifest, TestContext.Current.CancellationToken);
        var existingManifest = await store.GetGroupManifestAsync(existingDestination.Id,
            TestContext.Current.CancellationToken);
        var rejectExisting = () => store.ForkGroupedRunAsync(source.Id, existingDestination, manifest, "new-step",
            StepRestartMode.Dependents, null, null, TestContext.Current.CancellationToken).AsTask();
        await rejectExisting.Should().ThrowAsync<WorkflowStateException>();
        (await store.GetGroupManifestAsync(existingDestination.Id, TestContext.Current.CancellationToken))
            .Should().BeEquivalentTo(existingManifest);
        (await store.GetRunAsync(source.Id, TestContext.Current.CancellationToken)).Should().Be(before);

        await using (var connection = new SqliteConnection($"Data Source={Path.Combine(root, "zhinu.db")};Pooling=False"))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var update = connection.CreateCommand();
            update.CommandText = "UPDATE workflow_runs SET group_fanout_used = 1 WHERE id = $run;";
            update.Parameters.AddWithValue("$run", source.Id.ToString("D"));
            await update.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
        var rejectFanOut = () => store.ForkGroupedRunAsync(source.Id, destination, manifest, "new-step",
            StepRestartMode.Dependents, null, null, TestContext.Current.CancellationToken).AsTask();
        await rejectFanOut.Should().ThrowAsync<WorkflowStateException>();
        (await store.GetRunAsync(destination.Id, TestContext.Current.CancellationToken)).Should().BeNull();
        var sourceAfterFanOutReject = await store.GetRunAsync(source.Id, TestContext.Current.CancellationToken);
        sourceAfterFanOutReject!.GroupFanOutUsed.Should().BeTrue();
        sourceAfterFanOutReject.GroupManifestDigest.Should().Be(before!.GroupManifestDigest);
    }

    [Fact]
    public async Task GroupedFork_ReboundDataSurvivesSourceRetentionPurge()
    {
        var store = CreateStore();
        var manifest = CreateManifest("Source", childOrdinal: 1);
        var source = CreateRun(manifest);
        await store.CreateGroupedRunAsync(source, manifest, TestContext.Current.CancellationToken);
        var occurrence = ActivityGroupOccurrence.Create(source.Id, manifest, "outer", null, "[]");
        var claim = await store.ClaimStepAsync(new StepClaimRequest
        {
            WorkflowRunId = source.Id,
            StepKey = "work",
            OutputType = "output-v1",
            OwnerId = "worker",
            Now = DateTimeOffset.UtcNow.AddDays(-10),
            LeaseExpiresAt = DateTimeOffset.UtcNow.AddDays(-10).AddMinutes(1),
            GroupManifestDigest = manifest.ManifestDigest,
            Group = new ActivityGroupStepBinding(new ActivityGroupMembership("outer"), occurrence)
        }, TestContext.Current.CancellationToken);
        await store.CompleteStepAsync(claim.Step.Id, "worker", "done", DateTimeOffset.UtcNow.AddDays(-10),
            TestContext.Current.CancellationToken);
        await using (var connection = new SqliteConnection($"Data Source={Path.Combine(root, "zhinu.db")};Pooling=False"))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var update = connection.CreateCommand();
            update.CommandText = "UPDATE workflow_runs SET status = $status, updated_at = $updated, completed_at = $updated WHERE id = $run;";
            update.Parameters.AddWithValue("$status", (int)WorkflowStatus.Completed);
            update.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.AddDays(-10).ToString("O"));
            update.Parameters.AddWithValue("$run", source.Id.ToString("D"));
            await update.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
        var destination = CreateDestination(source, manifest) with
        {
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        await store.ForkGroupedRunAsync(source.Id, destination, manifest, "new-step",
            StepRestartMode.Dependents, null, null, TestContext.Current.CancellationToken);

        var purged = await store.PurgeRetainedRunsAsync(new RunRetentionOptions
        {
            OlderThan = DateTimeOffset.UtcNow.AddDays(-1),
            Statuses = [WorkflowStatus.Completed]
        }, TestContext.Current.CancellationToken);

        purged.Should().Be(1);
        (await store.GetRunAsync(source.Id, TestContext.Current.CancellationToken)).Should().BeNull();
        var copiedStep = (await store.GetStepsAsync(destination.Id, TestContext.Current.CancellationToken)).Single();
        var copiedMembership = await store.GetStepGroupMembershipAsync(copiedStep.Id,
            TestContext.Current.CancellationToken);
        copiedMembership.Should().NotBeNull();
        (await store.GetGroupOccurrenceAsync(destination.Id, copiedMembership!.GroupOccurrenceId,
            TestContext.Current.CancellationToken)).Should().NotBeNull();
    }

    [Fact]
    public async Task Schema8Migration_PreservesGroupedSourceAndAllowsDestinationOwnedFork()
    {
        var databasePath = Path.Combine(root, "zhinu.db");
        var store = CreateStore();
        var manifest = CreateManifest("Migration", childOrdinal: 3);
        var source = CreateRun(manifest) with { AuthoritySubjectBinding = "host:subject" };
        await store.CreateGroupedRunAsync(source, manifest, TestContext.Current.CancellationToken);
        var parent = ActivityGroupOccurrence.Create(source.Id, manifest, "outer", null, "[]");
        var child = ActivityGroupOccurrence.Create(source.Id, manifest, "inner", parent,
            "[{\"loop\":\"batch\",\"iteration\":4}]");
        var now = DateTimeOffset.UtcNow;
        var groupedClaim = await store.ClaimStepAsync(new StepClaimRequest
        {
            WorkflowRunId = source.Id,
            StepKey = "grouped-work",
            ImplementationKey = "group-worker",
            InputJson = "{\"item\":4}",
            InputType = "input-v1",
            InputHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            OutputType = "output-v1",
            OwnerId = "worker",
            Now = now,
            LeaseExpiresAt = now.AddMinutes(1),
            LeaseGeneration = 1,
            GroupManifestDigest = manifest.ManifestDigest,
            Group = new ActivityGroupStepBinding(
                new ActivityGroupMembership("inner", ActivityGroupMemberRole.Work), child)
        }, TestContext.Current.CancellationToken);
        await store.CompleteStepAsync(groupedClaim.Step.Id, "worker", "grouped-result", now.AddSeconds(1),
            TestContext.Current.CancellationToken);
        var ordinaryClaim = await store.ClaimStepAsync(new StepClaimRequest
        {
            WorkflowRunId = source.Id,
            StepKey = "ordinary-work",
            ImplementationKey = "ordinary-worker",
            InputJson = "ordinary-input",
            InputType = "input-v1",
            InputHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            OutputType = "output-v1",
            OwnerId = "worker",
            Now = now.AddSeconds(2),
            LeaseExpiresAt = now.AddMinutes(1),
            LeaseGeneration = 1,
            GroupManifestDigest = manifest.ManifestDigest
        }, TestContext.Current.CancellationToken);
        await store.CompleteStepAsync(ordinaryClaim.Step.Id, "worker", "ordinary-result", now.AddSeconds(3),
            TestContext.Current.CancellationToken);
        var sourceStepsBeforeMigration = await store.GetStepsAsync(source.Id, TestContext.Current.CancellationToken);
        var sourceMembershipBeforeMigration = await store.GetStepGroupMembershipAsync(groupedClaim.Step.Id,
            TestContext.Current.CancellationToken);

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
                UPDATE zhinu_schema SET version = 8 WHERE id = 1;
                """;
            await downgrade.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var upgraded = new SqliteWorkflowStore(new ZhinuSqliteOptions
        {
            DatabasePath = databasePath,
            Pooling = false
        });
        await upgraded.InitializeAsync(TestContext.Current.CancellationToken);
        await upgraded.InitializeAsync(TestContext.Current.CancellationToken);
        var restoredSource = await upgraded.GetRunAsync(source.Id, TestContext.Current.CancellationToken);
        restoredSource!.GroupManifestDigest.Should().Be(manifest.ManifestDigest);
        restoredSource.AuthoritySubjectBinding.Should().Be("host:subject");
        restoredSource.GroupFanOutUsed.Should().BeFalse();
        (await upgraded.GetGroupManifestAsync(source.Id, TestContext.Current.CancellationToken))!
            .CanonicalJson.Should().Be(manifest.CanonicalJson);
        (await upgraded.GetGroupOccurrenceAsync(source.Id, child.GroupOccurrenceId,
            TestContext.Current.CancellationToken))!.ParentGroupOccurrenceId.Should().Be(parent.GroupOccurrenceId);
        (await upgraded.GetStepGroupMembershipAsync(groupedClaim.Step.Id, TestContext.Current.CancellationToken))
            .Should().Be(sourceMembershipBeforeMigration);
        (await upgraded.GetStepsAsync(source.Id, TestContext.Current.CancellationToken))
            .Should().BeEquivalentTo(sourceStepsBeforeMigration);

        var destination = CreateDestination(source, manifest);
        var plan = await upgraded.ForkGroupedRunAsync(source.Id, destination, manifest, "new-step",
            StepRestartMode.Dependents, "operator", "schema migration check",
            TestContext.Current.CancellationToken);

        plan.StepsToReuse.Should().BeEquivalentTo(["grouped-work", "ordinary-work"]);
        var copiedSteps = await upgraded.GetStepsAsync(destination.Id, TestContext.Current.CancellationToken);
        copiedSteps.Should().HaveCount(2);
        foreach (var sourceStep in sourceStepsBeforeMigration)
        {
            var copied = copiedSteps.Single(step => step.StepKey == sourceStep.StepKey);
            copied.Id.Should().NotBe(sourceStep.Id);
            copied.WorkflowRunId.Should().Be(destination.Id);
            copied.Revision.Should().Be(1);
            copied.InputJson.Should().Be(sourceStep.InputJson);
            copied.InputHash.Should().Be(sourceStep.InputHash);
            copied.OutputJson.Should().Be(sourceStep.OutputJson);
            copied.ImplementationKey.Should().Be(sourceStep.ImplementationKey);
            var membership = await upgraded.GetStepGroupMembershipAsync(copied.Id,
                TestContext.Current.CancellationToken);
            if (sourceStep.StepKey == "ordinary-work")
            {
                membership.Should().BeNull();
                continue;
            }
            membership.Should().NotBeNull();
            membership!.WorkflowRunId.Should().Be(destination.Id);
            membership.StepId.Should().Be(copied.Id);
            membership.StepRevision.Should().Be(1);
            membership.GroupOccurrenceId.Should().NotBe(sourceMembershipBeforeMigration!.GroupOccurrenceId);
            var rebound = await upgraded.GetGroupOccurrenceAsync(destination.Id, membership.GroupOccurrenceId,
                TestContext.Current.CancellationToken);
            rebound.Should().NotBeNull();
            rebound!.ExecutionCoordinate.Should().Be(child.ExecutionCoordinate);
            rebound.ParentGroupOccurrenceId.Should().NotBe(parent.GroupOccurrenceId);
            (await upgraded.GetGroupOccurrenceAsync(destination.Id, rebound.ParentGroupOccurrenceId!,
                TestContext.Current.CancellationToken))!.GroupKey.Should().Be("outer");
        }
        var destinationRun = await upgraded.GetRunAsync(destination.Id, TestContext.Current.CancellationToken);
        destinationRun!.AuthoritySubjectBinding.Should().Be(restoredSource.AuthoritySubjectBinding);
        destinationRun.GroupManifestDigest.Should().Be(manifest.ManifestDigest);

        await using var verify = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        await verify.OpenAsync(TestContext.Current.CancellationToken);
        await using var schemaVersion = verify.CreateCommand();
        schemaVersion.CommandText = "SELECT version FROM zhinu_schema WHERE id = 1;";
        Convert.ToInt32(await schemaVersion.ExecuteScalarAsync(TestContext.Current.CancellationToken))
            .Should().Be(ZhinuSqliteSchema.CurrentVersion);
    }

    private static ActivityGroupManifest CreateManifest(string display, int childOrdinal) => ActivityGroupManifest.Create([
        new ActivityGroupDefinition("outer", ActivityGroupKind.Logical, display + " Outer"),
        new ActivityGroupDefinition("inner", ActivityGroupKind.Macro, display + " Inner", "outer", childOrdinal,
            new ActivityGroupMacroProvenance("macro", "fingerprint", "invocation", "v1", "source.cs"))
    ]);

    private static WorkflowRun CreateRun(ActivityGroupManifest? manifest)
    {
        var now = DateTimeOffset.UtcNow;
        return new WorkflowRun
        {
            Id = Guid.NewGuid(),
            WorkflowName = "group-fork-test",
            WorkflowVersion = "1",
            Status = WorkflowStatus.Pending,
            InputJson = "\"input\"",
            InputType = "input-v1",
            OutputType = "output-v1",
            CreatedAt = now,
            UpdatedAt = now,
            GroupManifestDigest = manifest?.ManifestDigest
        };
    }

    private static WorkflowRun CreateDestination(WorkflowRun source, ActivityGroupManifest? targetManifest)
    {
        var now = DateTimeOffset.UtcNow;
        return new WorkflowRun
        {
            Id = Guid.NewGuid(),
            WorkflowName = source.WorkflowName,
            WorkflowVersion = source.WorkflowVersion,
            Status = WorkflowStatus.Pending,
            InputJson = source.InputJson,
            InputType = source.InputType,
            OutputType = source.OutputType,
            CreatedAt = now,
            UpdatedAt = now,
            SourceRunId = source.Id,
            GroupManifestDigest = targetManifest?.ManifestDigest,
            AuthorizationProviderId = source.AuthorizationProviderId,
            AuthorizationBindingId = source.AuthorizationBindingId,
            AuthoritySubjectBinding = source.AuthoritySubjectBinding
        };
    }
}
