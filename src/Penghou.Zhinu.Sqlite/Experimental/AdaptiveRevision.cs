using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Penghou.Zhinu.Sqlite.Experimental;

/// <summary>Experimental M1 branch manifest. Host admission must verify the Fuwen plan and authority.</summary>
public sealed record AdaptiveRevision(
    Guid WorkflowId, string RevisionId, string? ParentRevisionId, string PlanEnvelopeJson,
    string Reason, string CreatedBy, AdaptiveBranch[] Branches);

/// <summary>Separate logical, revision-specific and execution identities with explicit equivalence evidence.</summary>
public sealed record AdaptiveBranch(
    string LogicalId, string RevisionStepId, string Title, string ContractDigest,
    string InputDigest, string AuthorityDigest, string Provenance,
    string[] Dependencies, WorkflowRun Execution, bool AcceptReuse);

public enum AdaptiveChangeKind { Preserved, Added, Removed, Modified, Superseded, DependencyChanged }
public enum AdaptiveDisposition { Continue, AllowToFinish, Cancel, Fence, SupersedeOnCompletion, RequiresManualDecision }
public enum AdaptiveAuthorityDecision { Automatic, ApprovalRequired, Denied }

public sealed record AdaptiveChange(string LogicalId, AdaptiveChangeKind Kind, AdaptiveDisposition Disposition);
public sealed record AdaptiveProposal(
    Guid ProposalId, AdaptiveRevision Candidate, string ParentStateDigest,
    AdaptiveChange[] Changes, AdaptiveAuthorityDecision Authority, string AuthorityEvidence,
    string ImpactSummary);

/// <summary>Deterministic conservative comparison; equality requires all relevant contracts and provenance.</summary>
public static class AdaptiveRevisionContracts
{
    public static string Digest<T>(T value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value)))).ToLowerInvariant();

    public static bool Equivalent(AdaptiveBranch before, AdaptiveBranch after) =>
        before.ContractDigest == after.ContractDigest && before.InputDigest == after.InputDigest &&
        before.AuthorityDigest == after.AuthorityDigest && before.Provenance == after.Provenance &&
        !string.IsNullOrWhiteSpace(after.Provenance) &&
        before.Dependencies.SequenceEqual(after.Dependencies, StringComparer.Ordinal) &&
        before.Execution.WorkflowName == after.Execution.WorkflowName &&
        before.Execution.WorkflowVersion == after.Execution.WorkflowVersion &&
        before.Execution.InputJson == after.Execution.InputJson &&
        before.Execution.InputType == after.Execution.InputType &&
        before.Execution.OutputType == after.Execution.OutputType &&
        before.Execution.DefinitionFingerprint == after.Execution.DefinitionFingerprint &&
        before.Execution.AuthorizationProviderId == after.Execution.AuthorizationProviderId &&
        before.Execution.AuthorizationBindingId == after.Execution.AuthorizationBindingId;

    public static AdaptiveChange[] Compare(AdaptiveRevision before, AdaptiveRevision after)
    {
        Validate(after);
        var old = before.Branches.ToDictionary(b => b.LogicalId, StringComparer.Ordinal);
        var next = after.Branches.ToDictionary(b => b.LogicalId, StringComparer.Ordinal);
        var changed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var branch in after.Branches)
            if (!old.TryGetValue(branch.LogicalId, out var prior) || !Equivalent(prior, branch))
                changed.Add(branch.LogicalId);
        // A changed prerequisite invalidates its transitive dependents, even when their names and input text match.
        bool more;
        do
        {
            more = false;
            foreach (var branch in after.Branches)
                if (branch.Dependencies.Any(changed.Contains)) more |= changed.Add(branch.LogicalId);
        } while (more);
        return old.Keys.Union(next.Keys).Order(StringComparer.Ordinal).Select(id =>
        {
            var hasOld = old.TryGetValue(id, out var prior);
            var hasNew = next.TryGetValue(id, out var branch);
            var kind = !hasOld ? AdaptiveChangeKind.Added : !hasNew ? AdaptiveChangeKind.Removed :
                !changed.Contains(id) ? AdaptiveChangeKind.Preserved :
                prior!.ContractDigest != branch!.ContractDigest || prior.InputDigest != branch.InputDigest ||
                prior.AuthorityDigest != branch.AuthorityDigest ? AdaptiveChangeKind.Modified :
                AdaptiveChangeKind.DependencyChanged;
            return new AdaptiveChange(id, kind, kind == AdaptiveChangeKind.Preserved ?
                AdaptiveDisposition.Continue : AdaptiveDisposition.Fence);
        }).ToArray();
    }

    public static void Validate(AdaptiveRevision revision)
    {
        if (revision.WorkflowId == Guid.Empty || string.IsNullOrWhiteSpace(revision.RevisionId) ||
            string.IsNullOrWhiteSpace(revision.PlanEnvelopeJson) || string.IsNullOrWhiteSpace(revision.Reason) ||
            string.IsNullOrWhiteSpace(revision.CreatedBy) || revision.Branches.Length is < 1 or > 64)
            throw new ArgumentException("A bounded, identified revision with a verified plan and rationale is required.");
        using var plan = JsonDocument.Parse(revision.PlanEnvelopeJson);
        var branches = revision.Branches.ToDictionary(b => b.LogicalId, StringComparer.Ordinal);
        if (revision.Branches.Select(b => b.RevisionStepId).Distinct().Count() != branches.Count ||
            revision.Branches.Select(b => b.Execution.Id).Distinct().Count() != branches.Count)
            throw new ArgumentException("Step and execution identities must be unique within a revision.");
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        void Visit(string id)
        {
            if (visited.Contains(id)) return;
            if (!branches.TryGetValue(id, out var branch) || !visiting.Add(id))
                throw new ArgumentException("Dependencies must resolve and be acyclic.");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(branch.RevisionStepId) ||
                string.IsNullOrWhiteSpace(branch.ContractDigest) || string.IsNullOrWhiteSpace(branch.InputDigest) ||
                string.IsNullOrWhiteSpace(branch.AuthorityDigest) || branch.Execution.Id == Guid.Empty)
                throw new ArgumentException("Every branch needs distinct identity and explicit equivalence inputs.");
            foreach (var dependency in branch.Dependencies) Visit(dependency);
            visiting.Remove(id);
            visited.Add(id);
        }
        foreach (var id in branches.Keys) Visit(id);
    }
}
