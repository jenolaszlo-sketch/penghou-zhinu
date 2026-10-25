using FluentAssertions;
using Microsoft.Data.Sqlite;

namespace Penghou.Zhinu.Sqlite.Tests;

public sealed class ActivityGroupBranchReplacementStoreTests : WorkflowEngineTestBase
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompetingReplacementsHaveOneWinnerAndRetiredClaimsCannotDispatch(bool emptyReplacement)
    {
        var store = CreateStore();
        var manifest = ActivityGroupManifest.Create([new ActivityGroupDefinition("branch", ActivityGroupKind.Logical, "Branch")]);
        var now = DateTimeOffset.UtcNow;
        var run = new WorkflowRun
        {
            Id = Guid.NewGuid(),
            WorkflowName = "branch",
            WorkflowVersion = "1",
            Status = WorkflowStatus.Running,
            CreatedAt = now,
            UpdatedAt = now,
            LeaseOwner = "worker",
            LeaseExpiresAt = now.AddHours(1),
            GroupManifestDigest = manifest.ManifestDigest
        };
        await store.CreateGroupedRunAsync(run, manifest, Ct);
        var boundary = Claim("selection", 1);
        var selected = await store.ClaimStepAsync(boundary, Ct);
        await store.CompleteStepAsync(selected.Step.Id, "worker", "{}", now, Ct);
        var originalPlan = Plan("a", null);
        var originalRequest = Request(originalPlan, 1);
        var original = await store.AdmitGroupFanOutAsync(originalRequest, Ct);
        var done = await store.ClaimStepAsync(ItemClaim(originalPlan.Items[0], original.ExpansionId, 1), Ct);
        await store.CompleteStepAsync(done.Step.Id, "worker", "{}", now, Ct);
        var doneBefore = (await store.GetStepsAsync(run.Id, Ct)).Single(step => step.Id == done.Step.Id);
        // Item two has never been materialized. Its retirement must still be durable.
        await store.RestartStepAsync(run.Id, "selection", StepRestartMode.StepOnly, null, null, now, Ct);
        var generation = (await store.TryClaimRunAsync(run.Id, "worker", now, now.AddHours(1), Ct))!.Value;
        var newBoundary = await store.ClaimStepAsync(boundary with { LeaseGeneration = generation }, Ct);
        await store.CompleteStepAsync(newBoundary.Step.Id, "worker", "{}", now, Ct);

        await store.Invoking(value => value.AdmitGroupFanOutAsync(Request(Plan("stale", "a"), 1), Ct).AsTask())
            .Should().ThrowAsync<LeaseLostException>();
        var candidates = new[] { Plan("b", "a"), Plan("c", "a") };
        var results = await Task.WhenAll(candidates.Select(async plan =>
        {
            try { return (Plan: plan, State: await store.AdmitGroupFanOutAsync(Request(plan, generation), Ct), Error: (Exception?)null); }
            catch (WorkflowStateException error) { return (Plan: plan, State: (ActivityGroupFanOutState?)null, Error: (Exception?)error); }
        }));
        var winner = results.Single(result => result.State is not null);
        results.Count(result => result.Error is WorkflowStateException).Should().Be(1);
        var replayed = await store.AdmitGroupFanOutAsync(Request(winner.Plan, generation), Ct);
        replayed.Should().BeEquivalentTo(winner.State);
        await store.Invoking(value => value.ClaimStepAsync(ItemClaim(originalPlan.Items[1], original.ExpansionId, generation), Ct).AsTask())
            .Should().ThrowAsync<WorkflowStateException>().WithMessage("*retired*");
        await store.Invoking(value => value.ClaimStepAsync(Claim("escape-retired-scope", generation) with
        {
            Group = originalPlan.Items[0].Group
        }, Ct).AsTask()).Should().ThrowAsync<WorkflowStateException>().WithMessage("*retired*");
        await store.Invoking(value => value.AdmitGroupFanOutAsync(Request(originalPlan, generation), Ct).AsTask())
            .Should().ThrowAsync<WorkflowStateException>().WithMessage("*retired*");
        await store.Invoking(value => value.AdmitGroupFanOutAsync(Request(Plan("too-soon", winner.Plan.FanOutKey), generation), Ct).AsTask())
            .Should().ThrowAsync<WorkflowStateException>().WithMessage("*newer revision*");

        foreach (var item in winner.Plan.Items)
        {
            var claimed = await store.ClaimStepAsync(ItemClaim(item, replayed.ExpansionId, generation), Ct);
            await store.CompleteStepAsync(claimed.Step.Id, "worker", "{}", now, Ct);
        }
        (await store.CompleteGroupFanOutAsync(Request(winner.Plan, generation), Ct)).IsComplete.Should().BeTrue();
        (await store.HasIncompleteGroupFanOutAsync(run.Id, Ct)).Should().BeFalse();
        (await store.GetStepsAsync(run.Id, Ct)).Single(step => step.Id == doneBefore.Id).Should().BeEquivalentTo(doneBefore);
        var snapshot = (await store.ReadGroupSnapshotAsync(run.Id, new(), Ct)).Value!;
        snapshot.Expansions.Should().HaveCount(2, "losing and invalid candidates must leave no rows");
        snapshot.Expansions.Single(expansion => expansion.FanOutKey == "a").Items.Should().HaveCount(2);
        (await store.GetEventsAsync(run.Id, 0, 1000, Ct)).Count(e => e.EventType == WorkflowEventTypes.GroupFanOutReplaced).Should().Be(1);

        var destination = run with { Id = Guid.NewGuid(), SourceRunId = run.Id, Status = WorkflowStatus.Pending };
        await store.Invoking(value => value.ForkGroupedRunAsync(run.Id, destination, manifest, "selection", StepRestartMode.Dependents,
            null, null, Ct).AsTask()).Should().ThrowAsync<WorkflowStateException>();
        (await store.GetRunAsync(destination.Id, Ct)).Should().BeNull();
        await using var connection = new SqliteConnection($"Data Source={Path.Combine(root, "zhinu.db")};Pooling=False;Foreign Keys=True");
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE workflow_group_fanout_retirements SET lease_generation=lease_generation+1;";
        await command.Invoking(value => value.ExecuteNonQueryAsync(Ct)).Should().ThrowAsync<SqliteException>();
        command.CommandText = "DELETE FROM workflow_runs WHERE id=$run; SELECT COUNT(*) FROM workflow_group_fanout_retirements WHERE workflow_run_id=$run;";
        command.Parameters.AddWithValue("$run", run.Id.ToString("D"));
        Convert.ToInt32(await command.ExecuteScalarAsync(Ct)).Should().Be(0);

        StepClaimRequest Claim(string key, long leaseGeneration) => new()
        {
            WorkflowRunId = run.Id,
            StepKey = key,
            InputType = "text",
            InputHash = Hash,
            OutputType = "text",
            OwnerId = "worker",
            Now = now,
            LeaseExpiresAt = now.AddHours(1),
            LeaseGeneration = leaseGeneration,
            GroupManifestDigest = manifest.ManifestDigest
        };
        StepClaimRequest ItemClaim(ActivityGroupFanOutItem item, string expansionId, long leaseGeneration) => Claim(item.StepKey, leaseGeneration) with
        {
            DependsOn = ["selection"],
            Group = item.Group,
            FanOut = new ActivityGroupFanOutItemClaim { ExpansionId = expansionId, ItemKey = item.ItemKey }
        };
        ActivityGroupFanOutPlan Plan(string key, string? replaces) => ActivityGroupFanOutPlan.Create(run.Id, manifest, key,
            new ActivityGroupFanOutBinding(new ActivityGroupMembership("branch"), "selection") { ReplacesFanOutKey = replaces },
            ActivityGroupFanOutIdentityKind.Keyed, "text", "text", null, null, "none", ["selection"],
            emptyReplacement && key != "a" ? [] : [("one", Hash), ("two", Hash)]);
        ActivityGroupFanOutRequest Request(ActivityGroupFanOutPlan plan, long leaseGeneration) => new()
        {
            Plan = plan,
            OwnerId = "worker",
            LeaseGeneration = leaseGeneration,
            Now = now
        };
    }
}
