using FluentAssertions;
using Microsoft.Data.Sqlite;

namespace Penghou.Zhinu.Sqlite.Tests;

public sealed class ActivityGroupFanOutAdmissionTests : WorkflowEngineTestBase
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("owner")]
    [InlineData("generation")]
    [InlineData("expired")]
    public async Task Admission_RejectsStaleFenceWithoutPartialEnumeration(string change)
    {
        var (store, request, _) = await ArrangeAsync();
        var stale = change switch
        {
            "owner" => request with { OwnerId = "other" },
            "generation" => request with { LeaseGeneration = request.LeaseGeneration + 1 },
            _ => request with { Now = request.Now.AddHours(1) }
        };
        var admit = () => store.AdmitGroupFanOutAsync(stale, Ct).AsTask();
        await admit.Should().ThrowAsync<LeaseLostException>();
        (await store.GetGroupFanOutAsync(request.Plan.WorkflowRunId, "items", Ct)).Should().BeNull();
        (await store.GetRunAsync(request.Plan.WorkflowRunId, Ct))!.GroupFanOutUsed.Should().BeFalse();
        (await CountAsync("workflow_group_occurrences")).Should().Be(0);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("expansion")]
    [InlineData("key")]
    [InlineData("input")]
    [InlineData("group")]
    [InlineData("type")]
    [InlineData("implementation")]
    [InlineData("authority")]
    [InlineData("dependencies")]
    [InlineData("compensation")]
    public async Task Claim_RejectsChangedAdmissionContractBeforeStepInsert(string change)
    {
        var (store, request, claim) = await ArrangeAsync();
        var state = await store.AdmitGroupFanOutAsync(request, Ct);
        claim = claim with { FanOut = new ActivityGroupFanOutItemClaim { ExpansionId = state.ExpansionId, ItemKey = "a" } };
        claim = change switch
        {
            "missing" => claim with { FanOut = null },
            "expansion" => claim with { FanOut = claim.FanOut! with { ExpansionId = "unknown" } },
            "key" => claim with { FanOut = claim.FanOut! with { ItemKey = "b" } },
            "input" => claim with { InputHash = "changed" },
            "group" => claim with { Group = null },
            "type" => claim with { OutputType = "other" },
            "implementation" => claim with { ImplementationKey = "other" },
            "authority" => claim with { AuthorizationDeclarationHash = new string('a', 64) },
            "dependencies" => claim with { DependsOn = [] },
            _ => claim with { Compensation = new CompensationMetadata("items.a", "{}", null) }
        };
        var acquire = () => store.ClaimStepAsync(claim, Ct).AsTask();
        await acquire.Should().ThrowAsync<WorkflowStateException>();
        (await store.GetStepsAsync(request.Plan.WorkflowRunId, Ct)).Select(step => step.StepKey).Should().Equal("select");
        (await CountAsync("workflow_step_group_memberships")).Should().Be(0);
        (await store.GetGroupFanOutAsync(request.Plan.WorkflowRunId, "items", Ct))!.IsComplete.Should().BeFalse();
    }

    [Fact]
    public async Task Completion_RequiresAllCurrentItemsAndCurrentLease()
    {
        var (store, request, claim) = await ArrangeAsync();
        var state = await store.AdmitGroupFanOutAsync(request, Ct);
        var completeEarly = () => store.CompleteGroupFanOutAsync(request, Ct).AsTask();
        await completeEarly.Should().ThrowAsync<WorkflowStateException>();
        var acquired = await store.ClaimStepAsync(claim with
        {
            FanOut = new ActivityGroupFanOutItemClaim { ExpansionId = state.ExpansionId, ItemKey = "a" }
        }, Ct);
        await store.CompleteStepAsync(acquired.Step.Id, claim.OwnerId, "\"A\"", request.Now, Ct);
        var stale = () => store.CompleteGroupFanOutAsync(request with { OwnerId = "other" }, Ct).AsTask();
        await stale.Should().ThrowAsync<LeaseLostException>();
        (await CountAsync("workflow_group_fanout_closures")).Should().Be(0);
        (await store.CompleteGroupFanOutAsync(request, Ct)).IsComplete.Should().BeTrue();
        (await store.CompleteGroupFanOutAsync(request, Ct)).IsComplete.Should().BeTrue();
        (await CountAsync("workflow_group_fanout_closures")).Should().Be(1);
    }

    [Fact]
    public async Task Admission_RejectsExistingNativeStepCollisionAtomically()
    {
        var (store, request, claim) = await ArrangeAsync();
        await store.ClaimStepAsync(claim with { Group = null, DependsOn = null }, Ct);
        var admit = () => store.AdmitGroupFanOutAsync(request, Ct).AsTask();
        await admit.Should().ThrowAsync<WorkflowStateException>();
        (await CountAsync("workflow_group_occurrences")).Should().Be(0);
        (await CountAsync("workflow_group_fanout_items")).Should().Be(0);
        (await store.GetRunAsync(request.Plan.WorkflowRunId, Ct))!.GroupFanOutUsed.Should().BeFalse();
    }

    private async Task<(SqliteWorkflowStore Store, ActivityGroupFanOutRequest Request, StepClaimRequest Claim)> ArrangeAsync()
    {
        var store = CreateStore();
        var now = DateTimeOffset.UtcNow;
        var manifest = ActivityGroupManifest.Create([new ActivityGroupDefinition("work", ActivityGroupKind.Logical, "Work")]);
        var runId = Guid.NewGuid();
        await store.CreateGroupedRunAsync(new WorkflowRun
        {
            Id = runId,
            WorkflowName = "admission",
            WorkflowVersion = "1",
            Status = WorkflowStatus.Pending,
            CreatedAt = now,
            UpdatedAt = now,
            GroupManifestDigest = manifest.ManifestDigest
        }, manifest, Ct);
        var generation = await store.TryClaimRunAsync(runId, "worker", now, now.AddMinutes(5), Ct);
        var selection = new StepClaimRequest
        {
            WorkflowRunId = runId,
            StepKey = "select",
            OutputType = "items",
            GroupManifestDigest = manifest.ManifestDigest,
            OwnerId = "worker",
            LeaseGeneration = generation!.Value,
            Now = now,
            LeaseExpiresAt = now.AddMinutes(5)
        };
        var boundary = await store.ClaimStepAsync(selection, Ct);
        await store.CompleteStepAsync(boundary.Step.Id, "worker", "[\"a\"]", now, Ct);
        var plan = ActivityGroupFanOutPlan.Create(runId, manifest, "items",
            new ActivityGroupFanOutBinding(new ActivityGroupMembership("work"), "select"),
            ActivityGroupFanOutIdentityKind.Keyed, "string", "string", null, null, "none", ["select"], [("a", "hash-a")]);
        var request = new ActivityGroupFanOutRequest { Plan = plan, OwnerId = "worker", LeaseGeneration = generation.Value, Now = now };
        var claim = selection with
        {
            StepKey = "items.a",
            InputType = "string",
            OutputType = "string",
            InputHash = "hash-a",
            InputJson = "\"a\"",
            Group = plan.Items.Single().Group,
            DependsOn = ["select"]
        };
        return (store, request, claim);
    }

    private async Task<int> CountAsync(string table)
    {
        await using var connection = new SqliteConnection($"Data Source={Path.Combine(root, "zhinu.db")};Pooling=False");
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM " + table;
        return Convert.ToInt32(await command.ExecuteScalarAsync(Ct));
    }
}
