using FluentAssertions;
using Microsoft.Data.Sqlite;
using Penghou.Zhinu.Sqlite.Experimental;

namespace Penghou.Zhinu.Sqlite.Tests;

public sealed class AdaptiveRevisionTests : WorkflowEngineTestBase
{
    private readonly SqliteDatabase database;
    private readonly SqliteWorkflowStore store;
    private readonly SqliteAdaptiveRevisionRepository revisions;

    public AdaptiveRevisionTests()
    {
        database = new SqliteDatabase(new ZhinuSqliteOptions
        {
            DatabasePath = Path.Combine(root, "adaptive.db"),
            BusyTimeout = TimeSpan.FromSeconds(2),
            Pooling = false
        });
        store = new SqliteWorkflowStore(database);
        revisions = new SqliteAdaptiveRevisionRepository(database);
    }

    [Fact]
    public async Task Commit_preserves_completed_and_running_executions_and_fences_invalidated_run()
    {
        var ct = TestContext.Current.CancellationToken;
        var initial = Revision("r1", null,
            Branch("done", "exec-done", acceptReuse: true),
            Branch("running", "exec-running"),
            Branch("change", "exec-change"));
        await ActivateAsync(initial);
        await SetRunState(initial.Branches[0].Execution.Id, WorkflowStatus.Completed, ct);
        await SetRunState(initial.Branches[1].Execution.Id, WorkflowStatus.Running, ct);
        var proposal = await Proposal(initial, Revision("r2", "r1",
            initial.Branches[0], initial.Branches[1], Branch("change", "exec-change-2", input: "changed")));
        var digest = await revisions.ProposeAsync(proposal, ct);
        await revisions.ApproveAsync(proposal.ProposalId, digest, "operator", "receipt", DateTimeOffset.UtcNow.AddMinutes(5), ct);

        await revisions.CommitAsync(proposal.ProposalId, digest, ct);

        (await store.GetRunAsync(initial.Branches[0].Execution.Id, ct))!.Status.Should().Be(WorkflowStatus.Completed);
        (await store.GetRunAsync(initial.Branches[1].Execution.Id, ct))!.Status.Should().Be(WorkflowStatus.Running);
        (await store.GetRunAsync(initial.Branches[2].Execution.Id, ct))!.Status.Should().Be(WorkflowStatus.Cancelled);
        (await store.GetRunAsync(proposal.Candidate.Branches[2].Execution.Id, ct)).Should().NotBeNull();
        await using var connection = await database.OpenAsync(ct);
        (await Scalar(connection, "SELECT COUNT(*) FROM workflow_runs", ct)).Should().Be(4);
    }

    [Fact]
    public async Task Completed_step_finish_from_old_generation_is_fenced_after_commit()
    {
        var ct = TestContext.Current.CancellationToken;
        var initial = Revision("r1", null, Branch("work", "work"));
        await ActivateAsync(initial);
        var runId = initial.Branches[0].Execution.Id;
        var claim = await store.ClaimStepAsync(new StepClaimRequest
        {
            WorkflowRunId = runId, StepKey = "slow", OutputType = "System.String",
            OwnerId = "worker", Now = DateTimeOffset.UtcNow,
            LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(1), LeaseGeneration = 1
        }, ct);
        claim.Disposition.Should().Be(StepClaimDisposition.Acquired);
        var proposal = await Proposal(initial, Revision("r2", "r1", Branch("work", "work-v2", input: "new")));
        var digest = await revisions.ProposeAsync(proposal, ct);
        await revisions.ApproveAsync(proposal.ProposalId, digest, "operator", "receipt", DateTimeOffset.UtcNow.AddMinutes(5), ct);
        await revisions.CommitAsync(proposal.ProposalId, digest, ct);

        var step = (await store.GetStepsAsync(runId, ct)).Single();
        var finish = () => store.CompleteStepAsync(step.Id, "worker", "\"late\"", DateTimeOffset.UtcNow, ct).AsTask();
        await finish.Should().ThrowAsync<WorkflowStateException>();
        (await store.GetStepsAsync(runId, ct)).Single().Status.Should().Be(StepStatus.Running);
    }

    [Fact]
    public async Task Approval_binds_exact_digest_and_commit_rejects_state_that_changed_after_approval()
    {
        var ct = TestContext.Current.CancellationToken;
        var initial = Revision("r1", null, Branch("work", "work"));
        await ActivateAsync(initial);
        var proposal = await Proposal(initial, Revision("r2", "r1", Branch("work", "work-v2", input: "changed")));
        var digest = await revisions.ProposeAsync(proposal, ct);
        var approveWrongDigest = () => revisions.ApproveAsync(proposal.ProposalId, "wrong", "operator", "receipt", DateTimeOffset.UtcNow.AddMinutes(5), ct);
        await approveWrongDigest.Should().ThrowAsync<WorkflowStateException>();
        await revisions.ApproveAsync(proposal.ProposalId, digest, "operator", "receipt", DateTimeOffset.UtcNow.AddMinutes(5), ct);
        await SetRunState(initial.Branches[0].Execution.Id, WorkflowStatus.Running, ct);

        var commit = () => revisions.CommitAsync(proposal.ProposalId, digest, ct);
        await commit.Should().ThrowAsync<WorkflowStateException>().WithMessage("Stale parent or execution state; build a new proposal.");
        (await revisions.GetActiveAsync(initial.WorkflowId, ct)).RevisionId.Should().Be("r1");
    }

    [Fact]
    public async Task Second_proposal_obsoletes_first_and_rejection_clears_pending_proposal()
    {
        var ct = TestContext.Current.CancellationToken;
        var initial = Revision("r1", null, Branch("work", "work"));
        await ActivateAsync(initial);
        var first = await Proposal(initial, Revision("r2", "r1", Branch("work", "work-v2", input: "one")));
        var firstDigest = await revisions.ProposeAsync(first, ct);
        var second = await Proposal(initial, Revision("r3", "r1", Branch("work", "work-v3", input: "two")));
        var secondDigest = await revisions.ProposeAsync(second, ct);
        (await revisions.GetPendingAsync(initial.WorkflowId, ct))!.ProposalId.Should().Be(second.ProposalId);
        var approveOld = () => revisions.ApproveAsync(first.ProposalId, firstDigest, "operator", "receipt", DateTimeOffset.UtcNow.AddMinutes(5), ct);
        await approveOld.Should().ThrowAsync<WorkflowStateException>();
        await revisions.RejectAsync(second.ProposalId, secondDigest, "operator", ct);

        (await revisions.GetActiveAsync(initial.WorkflowId, ct)).RevisionId.Should().Be("r1");
        var evidence = await revisions.GetEvidenceAsync(initial.WorkflowId, ct);
        evidence.Should().Contain(x => x.Contains("RevisionRejected", StringComparison.Ordinal));
        (await revisions.GetPendingAsync(initial.WorkflowId, ct)).Should().BeNull();
        (await revisions.GetHistoryAsync(initial.WorkflowId, ct)).Should().ContainSingle().Which.RevisionId.Should().Be("r1");
        await revisions.DiscussAsync(initial.WorkflowId, "operator", "Need a better input source", ct);
        (await revisions.GetEvidenceAsync(initial.WorkflowId, ct)).Should().Contain(x => x.Contains("OperatorDiscussion", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Reuse_requires_acceptance_and_changes_to_contract_or_dependencies_invalidate()
    {
        var ct = TestContext.Current.CancellationToken;
        var initial = Revision("r1", null, Branch("base", "base"), Branch("child", "child", dependencies: ["base"]));
        await ActivateAsync(initial);
        await SetRunState(initial.Branches[0].Execution.Id, WorkflowStatus.Completed, ct);

        var withoutReuse = await Proposal(initial, Revision("r2", "r1",
            initial.Branches[0] with { AcceptReuse = false }, initial.Branches[1]));
        var propose = () => revisions.ProposeAsync(withoutReuse, ct);
        await propose.Should().ThrowAsync<WorkflowStateException>().WithMessage("Completed work requires explicit reuse acceptance.");

        var changed = Revision("r2", "r1",
            initial.Branches[0] with { ContractDigest = "contract-v2" }, initial.Branches[1]);
        var comparison = AdaptiveRevisionContracts.Compare(initial, changed);
        comparison.Should().Contain(c => c.LogicalId == "base" && c.Kind == AdaptiveChangeKind.Modified);
        comparison.Should().Contain(c => c.LogicalId == "child" && c.Kind == AdaptiveChangeKind.DependencyChanged);
    }

    [Fact]
    public async Task Denied_proposal_cannot_be_approved_expired_approval_cannot_be_committed_and_collision_rolls_back()
    {
        var ct = TestContext.Current.CancellationToken;
        var initial = Revision("r1", null, Branch("work", "work"));
        await ActivateAsync(initial);
        var denied = await Proposal(initial, Revision("r2", "r1", Branch("work", "work-v2", input: "denied")), AdaptiveAuthorityDecision.Denied);
        var deniedDigest = await revisions.ProposeAsync(denied, ct);
        var denyApprove = () => revisions.ApproveAsync(denied.ProposalId, deniedDigest, "operator", "receipt", DateTimeOffset.UtcNow.AddMinutes(5), ct);
        await denyApprove.Should().ThrowAsync<WorkflowStateException>().WithMessage("Revision is denied.");
        await revisions.RejectAsync(denied.ProposalId, deniedDigest, "operator", ct);

        var candidateRun = Branch("work", "collision", input: "replacement");
        var collision = await Proposal(initial, Revision("r3", "r1", candidateRun));
        var collisionDigest = await revisions.ProposeAsync(collision, ct);
        await revisions.ApproveAsync(collision.ProposalId, collisionDigest, "operator", "receipt", DateTimeOffset.UtcNow.AddMinutes(5), ct);
        await InsertUnownedRun(candidateRun.Execution, ct);
        var commit = () => revisions.CommitAsync(collision.ProposalId, collisionDigest, ct);
        await commit.Should().ThrowAsync<SqliteException>();
        (await revisions.GetActiveAsync(initial.WorkflowId, ct)).RevisionId.Should().Be("r1");

        var fresh = await Proposal(initial, Revision("r4", "r1", Branch("work", "fresh", input: "fresh")));
        var freshDigest = await revisions.ProposeAsync(fresh, ct);
        await revisions.ApproveAsync(fresh.ProposalId, freshDigest, "operator", "receipt", DateTimeOffset.UtcNow.AddMilliseconds(500), ct);
        await Task.Delay(600, ct);
        var expiredCommit = () => revisions.CommitAsync(fresh.ProposalId, freshDigest, ct);
        await expiredCommit.Should().ThrowAsync<WorkflowStateException>().WithMessage("Revision requires an unexpired exact proposal approval.");
    }

    [Fact]
    public async Task Commit_survives_repository_and_store_reopen()
    {
        var ct = TestContext.Current.CancellationToken;
        var initial = Revision("r1", null, Branch("work", "work"));
        await ActivateAsync(initial);
        var proposal = await Proposal(initial, Revision("r2", "r1", Branch("work", "work-v2", input: "new")));
        var digest = await revisions.ProposeAsync(proposal, ct);
        await revisions.ApproveAsync(proposal.ProposalId, digest, "operator", "receipt", DateTimeOffset.UtcNow.AddMinutes(5), ct);
        await revisions.CommitAsync(proposal.ProposalId, digest, ct);

        var reopenedDatabase = new SqliteDatabase(new ZhinuSqliteOptions
        {
            DatabasePath = Path.Combine(root, "adaptive.db"), BusyTimeout = TimeSpan.FromSeconds(2), Pooling = false
        });
        var reopened = new SqliteAdaptiveRevisionRepository(reopenedDatabase);
        (await reopened.GetActiveAsync(initial.WorkflowId, ct)).RevisionId.Should().Be("r2");
        var retryCommit = () => reopened.CommitAsync(proposal.ProposalId, digest, ct);
        await retryCommit.Should().NotThrowAsync();
        (await new SqliteWorkflowStore(reopenedDatabase).GetRunAsync(proposal.Candidate.Branches[0].Execution.Id, ct)).Should().NotBeNull();
    }

    [Fact]
    public async Task Workflow_cancellation_fences_current_runs_and_prevents_new_proposals()
    {
        var ct = TestContext.Current.CancellationToken;
        var initial = Revision("r1", null, Branch("pending", "pending"), Branch("running", "running"));
        await ActivateAsync(initial);
        await SetRunState(initial.Branches[1].Execution.Id, WorkflowStatus.Running, ct);

        await revisions.CancelAsync(initial.WorkflowId, "operator", ct);

        (await store.GetRunAsync(initial.Branches[0].Execution.Id, ct))!.Status.Should().Be(WorkflowStatus.Cancelled);
        (await store.GetRunAsync(initial.Branches[1].Execution.Id, ct))!.Status.Should().Be(WorkflowStatus.Cancelled);
        var getActive = () => revisions.GetActiveAsync(initial.WorkflowId, ct);
        await getActive.Should().ThrowAsync<WorkflowStateException>();
    }

    [Fact]
    public async Task Runtime_lease_gate_keeps_join_unclaimable_until_every_prerequisite_completes()
    {
        var ct = TestContext.Current.CancellationToken;
        var initial = Revision("r1", null,
            Branch("left", "left"),
            Branch("right", "right"),
            Branch("join", "join", dependencies: ["left", "right"]));
        await ActivateAsync(initial);
        var joinRun = initial.Branches.Single(b => b.LogicalId == "join").Execution.Id;
        var now = DateTimeOffset.UtcNow;

        (await store.TryClaimRunAsync(joinRun, "worker", now, now.AddMinutes(1), ct)).Should().BeNull();
        await SetRunState(initial.Branches.Single(b => b.LogicalId == "left").Execution.Id, WorkflowStatus.Completed, ct);
        (await store.TryClaimRunAsync(joinRun, "worker", now, now.AddMinutes(1), ct)).Should().BeNull();
        await SetRunState(initial.Branches.Single(b => b.LogicalId == "right").Execution.Id, WorkflowStatus.Completed, ct);
        (await store.TryClaimRunAsync(joinRun, "worker", now, now.AddMinutes(1), ct)).Should().NotBeNull();
    }

    [Fact]
    public async Task New_execution_witness_makes_approved_proposal_stale_without_run_or_step_changes()
    {
        var ct = TestContext.Current.CancellationToken;
        var initial = Revision("r1", null, Branch("work", "work"));
        await ActivateAsync(initial);
        var proposal = await Proposal(initial, Revision("r2", "r1", Branch("work", "work-v2", input: "changed")));
        var digest = await revisions.ProposeAsync(proposal, ct);
        await revisions.ApproveAsync(proposal.ProposalId, digest, "operator", "receipt", DateTimeOffset.UtcNow.AddMinutes(5), ct);

        await using (var connection = await database.OpenAsync(ct))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "INSERT INTO adaptive_execution_witness(run_id,witness_key,digest) VALUES($run,'qhost-progress','abc123')";
            command.Parameters.AddWithValue("$run", initial.Branches[0].Execution.Id.ToString("D"));
            await command.ExecuteNonQueryAsync(ct);
        }

        var commit = () => revisions.CommitAsync(proposal.ProposalId, digest, ct);
        await commit.Should().ThrowAsync<WorkflowStateException>().WithMessage("Stale parent or execution state; build a new proposal.");
        (await revisions.GetActiveAsync(initial.WorkflowId, ct)).RevisionId.Should().Be("r1");
        (await store.GetRunAsync(initial.Branches[0].Execution.Id, ct))!.Status.Should().Be(WorkflowStatus.Pending);
        (await store.GetRunAsync(proposal.Candidate.Branches[0].Execution.Id, ct)).Should().BeNull();
    }

    private AdaptiveRevision Revision(string id, string? parent, params AdaptiveBranch[] branches) =>
        new(Guid.NewGuid(), id, parent, "{}", "reviewed change", "test-host", branches);

    private async Task ActivateAsync(AdaptiveRevision revision)
    {
        await revisions.InitializeAsync(TestContext.Current.CancellationToken);
        await revisions.ActivateInitialAsync(revision, TestContext.Current.CancellationToken);
    }

    private async Task<AdaptiveProposal> Proposal(AdaptiveRevision active, AdaptiveRevision candidate,
        AdaptiveAuthorityDecision authority = AdaptiveAuthorityDecision.ApprovalRequired)
    {
        candidate = candidate with { WorkflowId = active.WorkflowId };
        var changes = AdaptiveRevisionContracts.Compare(active, candidate)
            .Select(c => c with { Disposition = c.Kind == AdaptiveChangeKind.Preserved ? AdaptiveDisposition.Continue : AdaptiveDisposition.Fence })
            .ToArray();
        return new AdaptiveProposal(Guid.NewGuid(), candidate,
            await revisions.GetStateDigestAsync(active.WorkflowId, TestContext.Current.CancellationToken), changes, authority, "host-authority", "Replace changed operation");
    }

    private AdaptiveBranch Branch(string logical, string execution, string input = "same", bool acceptReuse = false,
        string[]? dependencies = null) => new(
        logical, "step-" + execution, logical, "contract-v1", input, "authority-v1", "source-plan", dependencies ?? [],
        Run(Guid.NewGuid(), execution, input), acceptReuse);

    private static WorkflowRun Run(Guid id, string name, string input) => new()
    {
        Id = id, WorkflowName = name, WorkflowVersion = "1", Status = WorkflowStatus.Pending,
        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        InputJson = "\"" + input + "\"", InputType = "System.String", OutputType = "System.String"
    };

    private async Task SetRunState(Guid id, WorkflowStatus status, CancellationToken ct)
    {
        await using var connection = await database.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE workflow_runs SET status=$status WHERE id=$id";
        command.Parameters.AddWithValue("$status", (int)status);
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        await command.ExecuteNonQueryAsync(ct);
    }

    private async Task InsertUnownedRun(WorkflowRun run, CancellationToken ct)
    {
        await store.CreateRunAsync(run, ct);
    }

    private static async Task<long> Scalar(SqliteConnection connection, string sql, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct));
    }
}
