namespace Penghou.Zhinu;

public enum WorkflowGroupCoverage { Complete, Partial, Unknown }
public enum WorkflowGroupExtent { Open, Closed, Interrupted }
public enum WorkflowGroupStateReason
{
    IncompleteData, HistoricalSelection, FailedMember, RunningMember, AwaitingRecovery,
    Approval, Signal, Child, Retry, Delay, OtherWait, AwaitingDispatch, Completed,
    Interrupted, DependencyBlocked, Quiescing, RunFailed, NotStarted, ExpansionOpen,
    ActiveOperation, Compensated, Superseded
}

/// <summary>A selected direct or descendant member, including one not yet materialized.</summary>
public sealed record WorkflowGroupSelectedMember(string StepKey, ActivityGroupMemberRole Role);

/// <summary>Authoritative caller-supplied selection/extent at one read boundary. Child-run interiors are excluded.</summary>
public sealed record WorkflowGroupStateInput
{
    public required WorkflowGroupRunFact Run { get; init; }
    public required IReadOnlyList<WorkflowGroupSelectedMember> Members { get; init; }
    public required IReadOnlyList<WorkflowGroupStepFact> Steps { get; init; }
    public WorkflowGroupExtent Extent { get; init; }
    public WorkflowGroupCoverage Coverage { get; init; } = WorkflowGroupCoverage.Complete;
    public IReadOnlyList<StepDependency> Dependencies { get; init; } = [];
    public IReadOnlyList<WorkflowGroupWaitFact> Waits { get; init; } = [];
    public IReadOnlyList<WorkflowGroupApprovalFact> Approvals { get; init; } = [];
    public WorkflowGroupGenerationFact? Generation { get; init; }
    public WorkflowGroupOperationFact? Operation { get; init; }
}

public sealed record WorkflowGroupStateCounts(int Known, int Pending, int Running, int Completed,
    int Failed, int Waiting, int Cancelled);

/// <summary>A diagnostic projection, never an execution state machine or acceptance decision.</summary>
public sealed record WorkflowGroupState(StepStatus? Status, WorkflowGroupStateReason Reason,
    bool IsBlocked, WorkflowGroupCoverage Coverage, bool IsExpansionClosed, int? TotalCount,
    WorkflowGroupStateCounts Work, WorkflowGroupStateCounts Control,
    IReadOnlyList<WorkflowGroupStateReason> Reasons, IReadOnlyList<string> ActiveStepKeys,
    IReadOnlyList<string> BlockingStepKeys, WorkflowStatus RunStatus,
    WorkflowGroupGenerationFact? Generation, WorkflowGroupOperationFact? Operation);

public sealed record WorkflowGroupStateSnapshot(string SnapshotToken, long ThroughDurableSequence,
    DateTimeOffset ObservedAt, string OccurrenceId, WorkflowGroupState State);

/// <summary>Additive optional capability; uses the same authorization and immutable token as structural reads.</summary>
public interface IWorkflowGroupStateReader
{
    ValueTask<WorkflowGroupQueryResult<WorkflowGroupStateSnapshot>> GetGroupStateAsync(Guid workflowRunId,
        string snapshotToken, string occurrenceId, CancellationToken cancellationToken = default);
}

public static class WorkflowGroupStateReducer
{
    /// <summary>No I/O or ambient clock. Selection and extent must come from authoritative, coherent facts.</summary>
    public static WorkflowGroupState Evaluate(WorkflowGroupStateInput input, DateTimeOffset observedAt) =>
        Evaluate(input, observedAt, null);

    internal static WorkflowGroupState Evaluate(WorkflowGroupStateInput input, DateTimeOffset observedAt, Facts? facts)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.Run);
        var reasons = new SortedSet<WorkflowGroupStateReason>();
        var active = new SortedSet<string>(StringComparer.Ordinal);
        var blockers = new SortedSet<string>(StringComparer.Ordinal);
        var work = new int[6];
        var control = new int[6];
        var selected = new Dictionary<string, ActivityGroupMemberRole>(StringComparer.Ordinal);

        WorkflowGroupState Result(StepStatus? status, WorkflowGroupStateReason reason, bool blocked = false,
            bool invalid = false)
        {
            if (invalid) { Array.Clear(work); Array.Clear(control); reasons.Clear(); active.Clear(); blockers.Clear(); }
            reasons.Add(reason);
            if (input.Run.Status == WorkflowStatus.Compensated) reasons.Add(WorkflowGroupStateReason.Compensated);
            if (input.Operation is not null || input.Run.Status == WorkflowStatus.RollingBack) reasons.Add(WorkflowGroupStateReason.ActiveOperation);
            var coverage = invalid ? WorkflowGroupCoverage.Unknown : input.Coverage;
            var closed = !invalid && coverage == WorkflowGroupCoverage.Complete && input.Extent == WorkflowGroupExtent.Closed;
            WorkflowGroupStateCounts Counts(int[] counts) => new(counts.Sum(), counts[0], counts[1], counts[2], counts[3], counts[4], counts[5]);
            return new(status, reason, blocked, coverage, closed, closed ? work.Sum() : null, Counts(work), Counts(control),
                reasons.ToList().AsReadOnly(), active.ToList().AsReadOnly(), blockers.ToList().AsReadOnly(),
                input.Run.Status, input.Generation, input.Operation);
        }

        if (input.Run.RunId == Guid.Empty || !Enum.IsDefined(input.Extent) || !Enum.IsDefined(input.Coverage) || !Enum.IsDefined(input.Run.Status)
            || input.Members is null || input.Steps is null || input.Dependencies is null || input.Waits is null || input.Approvals is null)
            return Result(null, WorkflowGroupStateReason.IncompleteData, invalid: true);
        foreach (var member in input.Members)
            if (member is null || string.IsNullOrWhiteSpace(member.StepKey) || !Enum.IsDefined(member.Role)
                || !selected.TryAdd(member.StepKey, member.Role)) return Result(null, WorkflowGroupStateReason.IncompleteData, invalid: true);
        facts ??= Prepare(input);
        if (facts is null) return Result(null, WorkflowGroupStateReason.IncompleteData, invalid: true);
        var current = facts.Current;
        var waits = facts.Waits;
        var approvals = facts.Approvals;
        var waitCandidates = new List<(int Priority, WorkflowGroupStateReason Reason, bool Ready)>();
        var dependencies = facts.Dependencies;
        foreach (var (key, role) in selected)
        {
            current.TryGetValue(key, out var step);
            var status = step?.Status ?? StepStatus.Pending;
            if (step?.Role is { } actualRole && actualRole != role) return Result(null, WorkflowGroupStateReason.IncompleteData, invalid: true);
            (role == ActivityGroupMemberRole.Work ? work : control)[(int)status]++;
            if (status == StepStatus.Running)
            {
                active.Add(key);
                reasons.Add(WorkflowGroupStateReason.RunningMember);
                if (step!.LeaseExpiresAt is null || step.LeaseExpiresAt <= observedAt ||
                    (input.Run.Status == WorkflowStatus.Running && (input.Run.LeaseExpiresAt is null || input.Run.LeaseExpiresAt <= observedAt)))
                    reasons.Add(WorkflowGroupStateReason.AwaitingRecovery);
            }
            if (status == StepStatus.Failed) reasons.Add(WorkflowGroupStateReason.FailedMember);
            if (status == StepStatus.Pending)
                foreach (var edge in dependencies[key])
                    if (!current.TryGetValue(edge.DependsOnStepKey, out var dependency) || dependency.Status != StepStatus.Completed)
                        blockers.Add(edge.DependsOnStepKey);
            if (step is null || status is StepStatus.Completed or StepStatus.Failed or StepStatus.Cancelled) continue;
            var previousWaits = waitCandidates.Count;
            foreach (var approval in approvals[(key, step.Revision)])
                if (approval.Decision == Penghou.Workflow.Abstractions.ExecutionAuthorizationDecision.ApprovalRequired)
                    waitCandidates.Add((0, WorkflowGroupStateReason.Approval, approval.Ready));
            foreach (var wait in waits[(step.StepId, step.Revision)])
            {
                if (wait.Status is not (WaitStatus.Parked or WaitStatus.Ready)) continue;
                if (wait.StepKey != key || !Enum.IsDefined(wait.Kind)) return Result(null, WorkflowGroupStateReason.IncompleteData, invalid: true);
                var kind = wait.Kind switch
                {
                    WaitKind.Signal => WorkflowGroupStateReason.Signal,
                    WaitKind.Child => WorkflowGroupStateReason.Child,
                    WaitKind.Retry => WorkflowGroupStateReason.Retry,
                    _ => WorkflowGroupStateReason.Delay
                };
                var ready = wait.Status == WaitStatus.Ready ||
                    (wait.Kind is WaitKind.Retry or WaitKind.Delay && wait.AvailableAt <= observedAt) || wait.DeadlineAt <= observedAt;
                waitCandidates.Add(((int)kind, kind, ready));
            }
            if (status == StepStatus.Waiting && waitCandidates.Count == previousWaits)
                waitCandidates.Add((99, WorkflowGroupStateReason.OtherWait, step.AvailableAt <= observedAt));
        }
        foreach (var candidate in waitCandidates)
        {
            reasons.Add(candidate.Reason);
            if (candidate.Ready) reasons.Add(WorkflowGroupStateReason.AwaitingDispatch);
        }
        if (input.Coverage != WorkflowGroupCoverage.Complete) return Result(null, WorkflowGroupStateReason.IncompleteData);
        if (work[3] + control[3] > 0) return Result(StepStatus.Failed, WorkflowGroupStateReason.FailedMember);
        if (active.Count > 0) return Result(StepStatus.Running, WorkflowGroupStateReason.RunningMember);
        if (waitCandidates.Count > 0 || work[4] + control[4] > 0)
        {
            var first = waitCandidates.OrderBy(wait => wait.Priority).ThenBy(wait => wait.Ready).FirstOrDefault();
            return Result(StepStatus.Waiting, first.Ready ? WorkflowGroupStateReason.AwaitingDispatch :
                waitCandidates.Count == 0 ? WorkflowGroupStateReason.OtherWait : first.Reason);
        }
        var allCompleted = work[2] + control[2] == selected.Count;
        if (input.Extent == WorkflowGroupExtent.Closed && allCompleted) return Result(StepStatus.Completed, WorkflowGroupStateReason.Completed);
        if ((input.Extent == WorkflowGroupExtent.Interrupted && input.Run.Status != WorkflowStatus.Failed) || work[5] + control[5] > 0 ||
            input.Run.Status == WorkflowStatus.Cancelled || input.Generation?.Status == WorkflowGenerationStatus.Superseded)
        {
            if (input.Generation?.Status == WorkflowGenerationStatus.Superseded) reasons.Add(WorkflowGroupStateReason.Superseded);
            return Result(StepStatus.Cancelled, WorkflowGroupStateReason.Interrupted);
        }
        if (blockers.Count > 0) return Result(StepStatus.Pending, WorkflowGroupStateReason.DependencyBlocked, true);
        if (input.Generation?.Status == WorkflowGenerationStatus.Quiescing) return Result(StepStatus.Pending, WorkflowGroupStateReason.Quiescing, true);
        if (input.Run.Status == WorkflowStatus.Failed) return Result(StepStatus.Pending, WorkflowGroupStateReason.RunFailed, true);
        if (input.Run.Status == WorkflowStatus.RollingBack || input.Operation is not null)
            return Result(StepStatus.Pending, WorkflowGroupStateReason.ActiveOperation, true);
        return Result(StepStatus.Pending, allCompleted || selected.Count == 0 ? WorkflowGroupStateReason.ExpansionOpen : WorkflowGroupStateReason.NotStarted);
    }

    // Prepared only from one frozen reader entry; the public pure reducer still validates every input.
    internal static Facts? Prepare(WorkflowGroupStateInput input)
    {
        if (input.Waits.Any(wait => wait is null || !Enum.IsDefined(wait.Kind) || !Enum.IsDefined(wait.Status))
            || input.Approvals.Any(approval => approval is null || !Enum.IsDefined(approval.Decision))
            || input.Dependencies.Any(edge => edge is null || string.IsNullOrWhiteSpace(edge.StepKey) || string.IsNullOrWhiteSpace(edge.DependsOnStepKey))) return null;
        var current = new Dictionary<string, WorkflowGroupStepFact>(StringComparer.Ordinal);
        var identities = new HashSet<Guid>();
        var revisions = new HashSet<(string, int)>();
        foreach (var step in input.Steps)
        {
            if (step is null || step.WorkflowRunId != input.Run.RunId || string.IsNullOrWhiteSpace(step.StepKey)
                || step.StepId == Guid.Empty || step.Revision < 1 || step.Attempt < 0 || !Enum.IsDefined(step.Status)
                || !identities.Add(step.StepId) || !revisions.Add((step.StepKey, step.Revision))) return null;
            if (step.IsCurrent && !current.TryAdd(step.StepKey, step)) return null;
        }
        foreach (var group in input.Steps.GroupBy(step => step.StepKey, StringComparer.Ordinal))
            if (!current.TryGetValue(group.Key, out var latest) || latest.Revision != group.Max(step => step.Revision)) return null;
        return new(current, input.Waits.ToLookup(wait => (wait.StepId, wait.Revision)),
            input.Approvals.Where(approval => !approval.IsCompensation).ToLookup(approval => (approval.StepKey, approval.Revision)),
            input.Dependencies.ToLookup(edge => edge.StepKey, StringComparer.Ordinal));
    }

    internal sealed record Facts(IReadOnlyDictionary<string, WorkflowGroupStepFact> Current,
        ILookup<(Guid, int), WorkflowGroupWaitFact> Waits,
        ILookup<(string, int), WorkflowGroupApprovalFact> Approvals, ILookup<string, StepDependency> Dependencies);
}
