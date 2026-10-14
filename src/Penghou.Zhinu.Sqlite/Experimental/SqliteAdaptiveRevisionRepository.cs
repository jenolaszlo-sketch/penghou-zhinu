using Microsoft.Data.Sqlite;
using Penghou.Zhinu.Sqlite.Persistence;
using Penghou.Zhinu.Sqlite.Persistence.Workflows;
using System.Text.Json;

namespace Penghou.Zhinu.Sqlite.Experimental;

/// <summary>
/// Experimental M1 runtime revision admission over independent branch runs. All mutators are trusted-host APIs.
/// Uses the same database owner as the workflow store. Fences, manifest activation and replacement run creation
/// share one SQLite transaction. No promise of rollback or fencing of external side effects.
/// </summary>
public sealed class SqliteAdaptiveRevisionRepository(IZhinuSqliteDatabase database)
{
    // Qualification-only seam, intentionally internal: lets a fresh-process probe die inside the
    // transaction after a fence write. Hosts cannot install policy or mutate execution through it.
    internal Action<string>? QualificationCheckpoint { get; set; }
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await database.EnsureInitializedAsync(cancellationToken);
        await using var connection = await database.OpenAsync(cancellationToken);
        await Execute(connection, null, """
            CREATE TABLE IF NOT EXISTS adaptive_workflows (
              id TEXT PRIMARY KEY, active_revision TEXT NOT NULL, pending_proposal TEXT, cancelled INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS adaptive_revisions (
              workflow_id TEXT NOT NULL, revision_id TEXT NOT NULL, document TEXT NOT NULL,
              proposal_id TEXT, PRIMARY KEY(workflow_id, revision_id));
            CREATE TABLE IF NOT EXISTS adaptive_proposals (
              id TEXT PRIMARY KEY, workflow_id TEXT NOT NULL, digest TEXT NOT NULL, document TEXT NOT NULL,
              status TEXT NOT NULL, approval_actor TEXT, approval_evidence TEXT, approval_expires TEXT);
            CREATE TABLE IF NOT EXISTS adaptive_run_ownership (
              run_id TEXT PRIMARY KEY, workflow_id TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS adaptive_execution_dependencies (
              run_id TEXT NOT NULL, prerequisite_run_id TEXT NOT NULL, PRIMARY KEY(run_id,prerequisite_run_id));
            CREATE TABLE IF NOT EXISTS adaptive_execution_witness (
              run_id TEXT NOT NULL, witness_key TEXT NOT NULL, digest TEXT NOT NULL, PRIMARY KEY(run_id,witness_key));
            CREATE TABLE IF NOT EXISTS adaptive_evidence (
              sequence INTEGER PRIMARY KEY AUTOINCREMENT, workflow_id TEXT NOT NULL,
              kind TEXT NOT NULL, document TEXT NOT NULL,
              recorded_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')));
            """, cancellationToken);
    }

    public async Task ActivateInitialAsync(AdaptiveRevision revision, CancellationToken cancellationToken = default)
    {
        // Serialize before awaiting: callers cannot mutate arrays while admission is in flight.
        revision = Copy(revision);
        AdaptiveRevisionContracts.Validate(revision);
        if (revision.ParentRevisionId is not null) throw new ArgumentException("Initial revision cannot have a parent.");
        await using var connection = await database.OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        var existing = await Scalar(connection, transaction,
            "SELECT document FROM adaptive_revisions WHERE workflow_id=$id AND revision_id=$rev", cancellationToken,
            ("$id", revision.WorkflowId.ToString("D")), ("$rev", revision.RevisionId));
        if (existing is not null)
        {
            if (existing != JsonSerializer.Serialize(revision)) throw new WorkflowStateException("Revision identity collision.");
            return;
        }
        await Execute(connection, transaction, "INSERT INTO adaptive_workflows(id,active_revision) VALUES($id,$rev)",
            cancellationToken, ("$id", revision.WorkflowId.ToString("D")), ("$rev", revision.RevisionId));
        foreach (var branch in revision.Branches) await AdmitRun(connection, transaction, revision.WorkflowId, branch.Execution, cancellationToken);
        await SaveDependencies(connection, transaction, revision, cancellationToken);
        await SaveRevision(connection, transaction, revision, null, cancellationToken);
        await Evidence(connection, transaction, revision.WorkflowId, "RevisionActivated", revision, cancellationToken);
        transaction.Commit();
    }

    public async Task<AdaptiveRevision> GetActiveAsync(Guid workflowId, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        return await Active(connection, transaction, workflowId, cancellationToken);
    }

    public async Task<string> GetStateDigestAsync(Guid workflowId, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        return await StateDigest(connection, transaction, await Active(connection, transaction, workflowId, cancellationToken), cancellationToken);
    }

    public async Task<string> ProposeAsync(AdaptiveProposal proposal, CancellationToken cancellationToken = default)
    {
        proposal = Copy(proposal);
        AdaptiveRevisionContracts.Validate(proposal.Candidate);
        if (proposal.ProposalId == Guid.Empty || string.IsNullOrWhiteSpace(proposal.AuthorityEvidence) ||
            string.IsNullOrWhiteSpace(proposal.ImpactSummary)) throw new ArgumentException("Proposal needs identity, authority evidence and impact.");
        var digest = AdaptiveRevisionContracts.Digest(proposal);
        await using var connection = await database.OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        var existing = await Scalar(connection, transaction, "SELECT digest FROM adaptive_proposals WHERE id=$p", cancellationToken,
            ("$p", proposal.ProposalId.ToString("D")));
        if (existing is not null)
        {
            if (existing != digest) throw new WorkflowStateException("Proposal identity is immutable.");
            return digest;
        }
        var active = await Active(connection, transaction, proposal.Candidate.WorkflowId, cancellationToken);
        await ValidateProposal(connection, transaction, active, proposal, cancellationToken);
        await Execute(connection, transaction,
            "UPDATE adaptive_proposals SET status='Obsolete' WHERE id=(SELECT pending_proposal FROM adaptive_workflows WHERE id=$id) AND status IN ('Pending','Approved')",
            cancellationToken, ("$id", active.WorkflowId.ToString("D")));
        await Execute(connection, transaction,
            "INSERT INTO adaptive_proposals(id,workflow_id,digest,document,status) VALUES($p,$id,$digest,$doc,'Pending')",
            cancellationToken, ("$p", proposal.ProposalId.ToString("D")), ("$id", active.WorkflowId.ToString("D")),
            ("$digest", digest), ("$doc", JsonSerializer.Serialize(proposal)));
        await Execute(connection, transaction, "UPDATE adaptive_workflows SET pending_proposal=$p WHERE id=$id", cancellationToken,
            ("$p", proposal.ProposalId.ToString("D")), ("$id", active.WorkflowId.ToString("D")));
        await Evidence(connection, transaction, active.WorkflowId, "RevisionProposalCreated", proposal, cancellationToken);
        transaction.Commit();
        return digest;
    }

    /// <summary>Host must authenticate the actor and bind Hufu's current decision to this exact digest.</summary>
    public async Task ApproveAsync(Guid proposalId, string digest, string actor, string authorityEvidence,
        DateTimeOffset expiresAt, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(actor) || string.IsNullOrWhiteSpace(authorityEvidence) || expiresAt <= database.TimeProvider.GetUtcNow())
            throw new ArgumentException("A current authenticated authority receipt is required.");
        await using var connection = await database.OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        var proposal = await Pending(connection, transaction, proposalId, digest, cancellationToken);
        if (proposal.Authority == AdaptiveAuthorityDecision.Denied) throw new WorkflowStateException("Revision is denied.");
        await ValidateProposal(connection, transaction,
            await Active(connection, transaction, proposal.Candidate.WorkflowId, cancellationToken), proposal, cancellationToken);
        await Execute(connection, transaction,
            "UPDATE adaptive_proposals SET status='Approved',approval_actor=$actor,approval_evidence=$e,approval_expires=$expires WHERE id=$p",
            cancellationToken, ("$actor", actor), ("$e", authorityEvidence), ("$expires", expiresAt.ToUniversalTime().ToString("O")),
            ("$p", proposalId.ToString("D")));
        await Evidence(connection, transaction, proposal.Candidate.WorkflowId, "RevisionApproved",
            new { proposalId, digest, actor, authorityEvidence, expiresAt }, cancellationToken);
        transaction.Commit();
    }

    public async Task CommitAsync(Guid proposalId, string digest, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        var committed = await Scalar(connection, transaction,
            "SELECT digest FROM adaptive_proposals WHERE id=$p AND status='Committed'", cancellationToken, ("$p", proposalId.ToString("D")));
        if (committed is not null)
        {
            if (committed != digest) throw new WorkflowStateException("Wrong committed proposal digest.");
            return;
        }
        var proposal = await Pending(connection, transaction, proposalId, digest, cancellationToken);
        var receipt = await Scalar(connection, transaction,
            "SELECT approval_expires FROM adaptive_proposals WHERE id=$p AND status='Approved'", cancellationToken, ("$p", proposalId.ToString("D")));
        if (receipt is null || DateTimeOffset.Parse(receipt) <= database.TimeProvider.GetUtcNow())
            throw new WorkflowStateException("Revision requires an unexpired exact proposal approval.");
        var active = await Active(connection, transaction, proposal.Candidate.WorkflowId, cancellationToken);
        await ValidateProposal(connection, transaction, active, proposal, cancellationToken);
        foreach (var change in proposal.Changes.Where(c => c.Kind != AdaptiveChangeKind.Preserved))
        {
            var branch = active.Branches.SingleOrDefault(b => b.LogicalId == change.LogicalId);
            if (branch is null) continue;
            // The run generation is the existing Zhinu step fence. Completed evidence remains immutable.
            await Execute(connection, transaction, """
                UPDATE workflow_runs SET lease_generation=lease_generation+1,lease_owner=NULL,lease_expires_at=NULL,
                  status=CASE WHEN status IN (0,1) THEN 4 ELSE status END,
                  updated_at=$now WHERE id=$run;
                """, cancellationToken, ("$run", branch.Execution.Id.ToString("D")),
                ("$now", database.TimeProvider.GetUtcNow().ToUniversalTime().ToString("O")));
            await Evidence(connection, transaction, active.WorkflowId, "StepFenced", new { branch.LogicalId, branch.Execution.Id, change.Disposition }, cancellationToken);
            QualificationCheckpoint?.Invoke("during-fencing");
        }
        foreach (var branch in proposal.Candidate.Branches)
            if (proposal.Changes.Single(c => c.LogicalId == branch.LogicalId).Kind != AdaptiveChangeKind.Preserved)
                await AdmitRun(connection, transaction, active.WorkflowId, branch.Execution, cancellationToken);
        await SaveDependencies(connection, transaction, proposal.Candidate, cancellationToken);
        await SaveRevision(connection, transaction, proposal.Candidate, proposalId, cancellationToken);
        await Execute(connection, transaction,
            "UPDATE adaptive_workflows SET active_revision=$rev,pending_proposal=NULL WHERE id=$id", cancellationToken,
            ("$rev", proposal.Candidate.RevisionId), ("$id", active.WorkflowId.ToString("D")));
        await Execute(connection, transaction, "UPDATE adaptive_proposals SET status='Committed' WHERE id=$p", cancellationToken,
            ("$p", proposalId.ToString("D")));
        await Evidence(connection, transaction, active.WorkflowId, "RevisionCommitted", new { proposalId, digest, proposal.Candidate.RevisionId }, cancellationToken);
        transaction.Commit();
    }

    public async Task RejectAsync(Guid proposalId, string digest, string actor, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        await using var connection = await database.OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        var proposal = await Pending(connection, transaction, proposalId, digest, cancellationToken);
        await Execute(connection, transaction, "UPDATE adaptive_proposals SET status='Rejected' WHERE id=$p", cancellationToken,
            ("$p", proposalId.ToString("D")));
        await Execute(connection, transaction, "UPDATE adaptive_workflows SET pending_proposal=NULL WHERE id=$id", cancellationToken,
            ("$id", proposal.Candidate.WorkflowId.ToString("D")));
        await Evidence(connection, transaction, proposal.Candidate.WorkflowId, "RevisionRejected", new { proposalId, digest, actor }, cancellationToken);
        transaction.Commit();
    }

    public async Task<IReadOnlyList<string>> GetEvidenceAsync(Guid workflowId, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = SqliteStoreSupport.CreateCommand(connection, null,
            "SELECT sequence,kind,document,recorded_at FROM adaptive_evidence WHERE workflow_id=$id ORDER BY sequence");
        command.Parameters.AddWithValue("$id", workflowId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<string>();
        while (await reader.ReadAsync(cancellationToken)) result.Add(JsonSerializer.Serialize(new
        { Sequence = reader.GetInt64(0), Kind = reader.GetString(1), Document = reader.GetString(2), RecordedAt = reader.GetString(3) }));
        return result;
    }

    public async Task<AdaptiveRevision[]> GetHistoryAsync(Guid workflowId, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = SqliteStoreSupport.CreateCommand(connection, null,
            "SELECT document FROM adaptive_revisions WHERE workflow_id=$id ORDER BY rowid");
        command.Parameters.AddWithValue("$id", workflowId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<AdaptiveRevision>();
        while (await reader.ReadAsync(cancellationToken)) result.Add(JsonSerializer.Deserialize<AdaptiveRevision>(reader.GetString(0))!);
        return result.ToArray();
    }

    public async Task<AdaptiveProposal?> GetPendingAsync(Guid workflowId, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        var json = await Scalar(connection, null,
            "SELECT p.document FROM adaptive_proposals p JOIN adaptive_workflows w ON w.pending_proposal=p.id WHERE w.id=$id AND w.cancelled=0",
            cancellationToken, ("$id", workflowId.ToString("D")));
        return json is null ? null : JsonSerializer.Deserialize<AdaptiveProposal>(json);
    }

    public async Task CancelAsync(Guid workflowId, string authenticatedActor, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(authenticatedActor);
        await using var connection = await database.OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        var active = await Active(connection, transaction, workflowId, cancellationToken);
        foreach (var branch in active.Branches)
            await Execute(connection, transaction, """
                UPDATE workflow_runs SET lease_generation=lease_generation+1,lease_owner=NULL,lease_expires_at=NULL,
                  status=CASE WHEN status IN (0,1) THEN 4 ELSE status END,updated_at=$now WHERE id=$run
                """, cancellationToken, ("$run", branch.Execution.Id.ToString("D")),
                ("$now", database.TimeProvider.GetUtcNow().ToUniversalTime().ToString("O")));
        await Execute(connection, transaction, "UPDATE adaptive_proposals SET status='Obsolete' WHERE id=(SELECT pending_proposal FROM adaptive_workflows WHERE id=$id)",
            cancellationToken, ("$id", workflowId.ToString("D")));
        await Execute(connection, transaction, "UPDATE adaptive_workflows SET cancelled=1,pending_proposal=NULL WHERE id=$id", cancellationToken,
            ("$id", workflowId.ToString("D")));
        await Evidence(connection, transaction, workflowId, "WorkflowCancelled", new { authenticatedActor, active.RevisionId }, cancellationToken);
        transaction.Commit();
    }

    public async Task DiscussAsync(Guid workflowId, string authenticatedActor, string question, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(authenticatedActor);
        if (string.IsNullOrWhiteSpace(question) || question.Length > 8192) throw new ArgumentException("Discussion must be bounded.");
        await using var connection = await database.OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        await Active(connection, transaction, workflowId, cancellationToken);
        await Evidence(connection, transaction, workflowId, "OperatorDiscussion", new { authenticatedActor, question }, cancellationToken);
        transaction.Commit();
    }

    private async Task ValidateProposal(SqliteConnection connection, SqliteTransaction transaction, AdaptiveRevision active,
        AdaptiveProposal proposal, CancellationToken ct)
    {
        if (proposal.Candidate.ParentRevisionId != active.RevisionId || proposal.Candidate.RevisionId == active.RevisionId ||
            proposal.ParentStateDigest != await StateDigest(connection, transaction, active, ct))
            throw new WorkflowStateException("Stale parent or execution state; build a new proposal.");
        var expected = AdaptiveRevisionContracts.Compare(active, proposal.Candidate);
        if (proposal.Changes.Length != expected.Length || proposal.Changes.Select(c => c.LogicalId).Distinct().Count() != expected.Length)
            throw new WorkflowStateException("Incomplete or ambiguous change set.");
        foreach (var change in expected)
        {
            var supplied = proposal.Changes.Single(c => c.LogicalId == change.LogicalId);
            if (supplied.Kind != change.Kind || supplied.Disposition == AdaptiveDisposition.RequiresManualDecision ||
                (change.Kind == AdaptiveChangeKind.Preserved
                    ? supplied.Disposition is not (AdaptiveDisposition.Continue or AdaptiveDisposition.AllowToFinish)
                    : supplied.Disposition != AdaptiveDisposition.Fence))
                throw new WorkflowStateException("Change classification or running disposition is invalid.");
            var before = active.Branches.SingleOrDefault(b => b.LogicalId == change.LogicalId);
            var after = proposal.Candidate.Branches.SingleOrDefault(b => b.LogicalId == change.LogicalId);
            if (change.Kind == AdaptiveChangeKind.Preserved)
            {
                if (before!.Execution.Id != after!.Execution.Id) throw new WorkflowStateException("Preserved work must retain its execution.");
                var status = await Scalar(connection, transaction, "SELECT status FROM workflow_runs WHERE id=$r", ct,
                    ("$r", before.Execution.Id.ToString("D")));
                if (status == ((int)WorkflowStatus.Completed).ToString() && !after.AcceptReuse)
                    throw new WorkflowStateException("Completed work requires explicit reuse acceptance.");
                if (status is not ("0" or "1" or "2")) throw new WorkflowStateException("Failed or cancelled work cannot be preserved.");
            }
            else if (after is not null && active.Branches.Any(b => b.Execution.Id == after.Execution.Id))
                throw new WorkflowStateException("Invalidated work requires a new execution identity.");
        }
    }

    private static async Task<AdaptiveRevision> Active(SqliteConnection c, SqliteTransaction t, Guid id, CancellationToken ct)
    {
        var json = await Scalar(c, t, """
            SELECT r.document FROM adaptive_revisions r JOIN adaptive_workflows w
            ON r.workflow_id=w.id AND r.revision_id=w.active_revision WHERE w.id=$id AND w.cancelled=0
            """, ct, ("$id", id.ToString("D")));
        return json is null ? throw new WorkflowStateException("No active adaptive workflow.") : JsonSerializer.Deserialize<AdaptiveRevision>(json)!;
    }

    private static async Task<AdaptiveProposal> Pending(SqliteConnection c, SqliteTransaction t, Guid id, string digest, CancellationToken ct)
    {
        var json = await Scalar(c, t, """
            SELECT p.document FROM adaptive_proposals p JOIN adaptive_workflows w ON w.pending_proposal=p.id
            WHERE p.id=$p AND p.digest=$digest AND p.status IN ('Pending','Approved') AND w.cancelled=0
            """, ct, ("$p", id.ToString("D")), ("$digest", digest));
        return json is null ? throw new WorkflowStateException("Unknown, obsolete or mismatched proposal.") : JsonSerializer.Deserialize<AdaptiveProposal>(json)!;
    }

    private static async Task<string> StateDigest(SqliteConnection c, SqliteTransaction t, AdaptiveRevision revision, CancellationToken ct)
    {
        var rows = new List<string> { AdaptiveRevisionContracts.Digest(revision) };
        foreach (var branch in revision.Branches.OrderBy(b => b.LogicalId, StringComparer.Ordinal))
        {
            // Exclude heartbeat times, but bind progress, attempts, generations and committed results.
            foreach (var sql in new[] {
                "SELECT id,status,lease_generation,output_json,error_json,definition_fingerprint FROM workflow_runs WHERE id=$r",
                "SELECT id,step_key,status,attempt,revision,lease_generation,input_json,output_json,error_json FROM workflow_steps WHERE workflow_run_id=$r ORDER BY step_key,revision,id",
                "SELECT witness_key,digest FROM adaptive_execution_witness WHERE run_id=$r ORDER BY witness_key" })
            {
                await using var command = SqliteStoreSupport.CreateCommand(c, t, sql);
                command.Parameters.AddWithValue("$r", branch.Execution.Id.ToString("D"));
                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                    rows.Add(JsonSerializer.Serialize(Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? null : reader.GetValue(i).ToString()).ToArray()));
            }
        }
        return AdaptiveRevisionContracts.Digest(rows);
    }

    private static async Task AdmitRun(SqliteConnection c, SqliteTransaction t, Guid workflowId, WorkflowRun run, CancellationToken ct)
    {
        if (run.Status != WorkflowStatus.Pending || run.LeaseOwner is not null || run.CompletedAt is not null ||
            run.OutputJson is not null || run.Error is not null || run.SourceRunId is not null || run.ParentRunId is not null)
            throw new ArgumentException("Replacement execution must be a new, pending independent branch.");
        await Execute(c, t, "INSERT INTO adaptive_run_ownership(run_id,workflow_id) VALUES($r,$w)", ct,
            ("$r", run.Id.ToString("D")), ("$w", workflowId.ToString("D")));
        await new InsertRunCommand().ExecuteAsync(c, t, run, ct);
    }

    private static Task SaveRevision(SqliteConnection c, SqliteTransaction t, AdaptiveRevision revision, Guid? proposalId, CancellationToken ct) =>
        Execute(c, t, "INSERT INTO adaptive_revisions(workflow_id,revision_id,document,proposal_id) VALUES($w,$r,$d,$p)", ct,
            ("$w", revision.WorkflowId.ToString("D")), ("$r", revision.RevisionId), ("$d", JsonSerializer.Serialize(revision)), ("$p", proposalId?.ToString("D")));
    private static async Task SaveDependencies(SqliteConnection c, SqliteTransaction t, AdaptiveRevision revision, CancellationToken ct)
    {
        foreach (var branch in revision.Branches)
            foreach (var dependency in branch.Dependencies)
                await Execute(c, t, "INSERT OR IGNORE INTO adaptive_execution_dependencies(run_id,prerequisite_run_id) VALUES($r,$p)", ct,
                    ("$r", branch.Execution.Id.ToString("D")),
                    ("$p", revision.Branches.Single(b => b.LogicalId == dependency).Execution.Id.ToString("D")));
    }
    private static Task Evidence<T>(SqliteConnection c, SqliteTransaction t, Guid workflowId, string kind, T value, CancellationToken ct) =>
        Execute(c, t, "INSERT INTO adaptive_evidence(workflow_id,kind,document) VALUES($w,$k,$d)", ct,
            ("$w", workflowId.ToString("D")), ("$k", kind), ("$d", JsonSerializer.Serialize(value)));
    private static T Copy<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;
    private static async Task Execute(SqliteConnection c, SqliteTransaction? t, string sql, CancellationToken ct, params (string Key, string? Value)[] args)
    {
        await using var command = SqliteStoreSupport.CreateCommand(c, t, sql);
        foreach (var (key, value) in args) command.Parameters.AddWithValue(key, value is null ? DBNull.Value : value);
        await command.ExecuteNonQueryAsync(ct);
    }
    private static async Task<string?> Scalar(SqliteConnection c, SqliteTransaction? t, string sql, CancellationToken ct, params (string Key, string? Value)[] args)
    {
        await using var command = SqliteStoreSupport.CreateCommand(c, t, sql);
        foreach (var (key, value) in args) command.Parameters.AddWithValue(key, value is null ? DBNull.Value : value);
        var result = await command.ExecuteScalarAsync(ct);
        return result is null or DBNull ? null : result.ToString();
    }
}
