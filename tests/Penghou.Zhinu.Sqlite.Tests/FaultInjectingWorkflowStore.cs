namespace Penghou.Zhinu.Sqlite.Tests;

/// <summary>Thrown by <see cref="FaultInjectingWorkflowStore"/> to simulate a deterministic crash at a durable boundary.</summary>
public sealed class FaultInjectedException : Exception
{
    public FaultInjectedException(string faultPoint)
        : base($"Injected fault at '{faultPoint}'.")
    {
        FaultPoint = faultPoint;
    }

    public string FaultPoint { get; }
}

/// <summary>
/// Test decorator that wraps an <see cref="IWorkflowStore"/> and deterministically
/// throws at named durable boundaries, simulating process death without OS-level
/// subprocess orchestration. A test can then construct a fresh engine over the same
/// underlying store to verify recovery.
/// </summary>
public sealed class FaultInjectingWorkflowStore : IWorkflowStore
{
    public const string AfterClaimPersisted = "AfterClaimPersisted";
    public const string BeforeStepCompletionCommit = "BeforeStepCompletionCommit";
    public const string AfterStepCompletionCommit = "AfterStepCompletionCommit";
    public const string BeforeCompensationCommit = "BeforeCompensationCommit";
    public const string AfterCompensationClaim = "AfterCompensationClaim";
    public const string BeforeRestartCommit = "BeforeRestartCommit";
    public const string BeforeRollbackTransition = "BeforeRollbackTransition";

    private readonly IWorkflowStore inner;
    private readonly Dictionary<string, int> armedFaults = new(StringComparer.Ordinal);

    public FaultInjectingWorkflowStore(IWorkflowStore inner)
    {
        this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    /// <summary>Arms a fault point to throw on the next <paramref name="count"/> hits (default once).</summary>
    public void Arm(string faultPoint, int count = 1)
    {
        if (count < 1)
            throw new ArgumentOutOfRangeException(nameof(count));
        armedFaults[faultPoint] = count;
    }

    /// <summary>Removes all armed faults.</summary>
    public void Reset() => armedFaults.Clear();

    private void FaultBefore(string faultPoint)
    {
        if (!armedFaults.TryGetValue(faultPoint, out var remaining))
            return;
        if (remaining <= 1)
            armedFaults.Remove(faultPoint);
        else
            armedFaults[faultPoint] = remaining - 1;
        if (remaining == 1)
            throw new FaultInjectedException(faultPoint);
    }

    private async ValueTask<T> FaultAfter<T>(string faultPoint, ValueTask<T> action)
    {
        var result = await action.ConfigureAwait(false);
        FaultBefore(faultPoint);
        return result;
    }

    private async ValueTask FaultAfter(string faultPoint, ValueTask action)
    {
        await action.ConfigureAwait(false);
        FaultBefore(faultPoint);
    }

    // ---- IWorkflowRepository ----

    public ValueTask InitializeAsync(CancellationToken ct = default) => inner.InitializeAsync(ct);

    public ValueTask CreateRunAsync(WorkflowRun run, CancellationToken ct = default) => inner.CreateRunAsync(run, ct);

    public ValueTask<WorkflowRun?> GetRunAsync(Guid id, CancellationToken ct = default) => inner.GetRunAsync(id, ct);

    public ValueTask<IReadOnlyList<WorkflowRun>> GetRunsAsync(RunQuery query, CancellationToken ct = default) => inner.GetRunsAsync(query, ct);

    public ValueTask<WorkflowRun?> UpdateRunMetadataAsync(Guid workflowRunId, string? metadataJson, CancellationToken ct = default) => inner.UpdateRunMetadataAsync(workflowRunId, metadataJson, ct);

    public ValueTask<IReadOnlyList<WorkflowRun>> GetRunSubtreeAsync(Guid workflowRunId, int maxDepth, CancellationToken ct = default) => inner.GetRunSubtreeAsync(workflowRunId, maxDepth, ct);

    public ValueTask<IReadOnlyList<WorkflowEvent>> GetEventsAsync(Guid workflowRunId, long afterSequence, int limit, CancellationToken ct = default) => inner.GetEventsAsync(workflowRunId, afterSequence, limit, ct);

    public ValueTask<WorkflowEvent> AppendEventAsync(Guid workflowRunId, string eventType, string? dataJson, string? stepKey = null, int? attempt = null, CancellationToken ct = default) => inner.AppendEventAsync(workflowRunId, eventType, dataJson, stepKey, attempt, ct);

    public ValueTask CompleteRunAsync(Guid workflowRunId, string ownerId, string? outputJson, string outputType, DateTimeOffset now, CancellationToken ct = default) => inner.CompleteRunAsync(workflowRunId, ownerId, outputJson, outputType, now, ct);

    public ValueTask FailRunAsync(Guid workflowRunId, string ownerId, WorkflowError error, DateTimeOffset now, CancellationToken ct = default) => inner.FailRunAsync(workflowRunId, ownerId, error, now, ct);

    public ValueTask CancelRunAsync(Guid workflowRunId, DateTimeOffset now, CancellationToken ct = default) => inner.CancelRunAsync(workflowRunId, now, ct);

    public ValueTask<int> PurgeRunsAsync(DateTimeOffset olderThan, IReadOnlyList<WorkflowStatus>? statuses = null, CancellationToken ct = default) => inner.PurgeRunsAsync(olderThan, statuses, ct);

    // ---- IWorkflowStepRepository ----

    public ValueTask<IReadOnlyList<WorkflowStepRun>> GetStepsAsync(Guid workflowRunId, CancellationToken ct = default) => inner.GetStepsAsync(workflowRunId, ct);

    public ValueTask<StepClaimResult> ClaimStepAsync(StepClaimRequest request, CancellationToken ct = default) =>
        FaultAfter(AfterClaimPersisted, inner.ClaimStepAsync(request, ct));

    public ValueTask<bool> RenewStepLeaseAsync(Guid stepId, string ownerId, DateTimeOffset leaseExpiresAt, CancellationToken ct = default) => inner.RenewStepLeaseAsync(stepId, ownerId, leaseExpiresAt, ct);

    public ValueTask CompleteStepAsync(Guid stepId, string ownerId, string? outputJson, DateTimeOffset now, CancellationToken ct = default)
    {
        FaultBefore(BeforeStepCompletionCommit);
        return FaultAfter(AfterStepCompletionCommit, inner.CompleteStepAsync(stepId, ownerId, outputJson, now, ct));
    }

    public ValueTask<IReadOnlyList<WorkflowEvent>> CompleteStepWithEventsAsync(Guid stepId, string ownerId, string? outputJson, DateTimeOffset now, IReadOnlyList<PendingWorkflowEvent>? events, CancellationToken ct = default)
    {
        FaultBefore(BeforeStepCompletionCommit);
        return FaultAfter(AfterStepCompletionCommit, inner.CompleteStepWithEventsAsync(stepId, ownerId, outputJson, now, events, ct));
    }

    public ValueTask FailStepAsync(Guid stepId, string ownerId, WorkflowError error, DateTimeOffset? retryAt, DateTimeOffset now, CancellationToken ct = default) => inner.FailStepAsync(stepId, ownerId, error, retryAt, now, ct);

    public ValueTask<IReadOnlyList<StepDependency>> GetStepDependenciesAsync(Guid workflowRunId, CancellationToken ct = default) => inner.GetStepDependenciesAsync(workflowRunId, ct);

    public ValueTask<IReadOnlyList<WorkflowStepCompensation>> GetCompensationsAsync(Guid workflowRunId, CancellationToken ct = default) => inner.GetCompensationsAsync(workflowRunId, ct);

    public ValueTask<RestartPlan> PlanRestartAsync(Guid workflowRunId, string stepKey, StepRestartMode mode, CancellationToken ct = default) => inner.PlanRestartAsync(workflowRunId, stepKey, mode, ct);

    public ValueTask<RestartPlan> RestartStepAsync(Guid workflowRunId, string stepKey, StepRestartMode mode, string? actor, string? reason, DateTimeOffset now, CancellationToken ct = default)
    {
        FaultBefore(BeforeRestartCommit);
        return inner.RestartStepAsync(workflowRunId, stepKey, mode, actor, reason, now, ct);
    }

    public ValueTask<RollbackPlan> PlanRollbackAsync(Guid workflowRunId, string? targetStepKey, RollbackBoundary boundary, CancellationToken ct = default) => inner.PlanRollbackAsync(workflowRunId, targetStepKey, boundary, ct);

    public ValueTask<long?> ClaimRollbackAsync(Guid workflowRunId, string ownerId, DateTimeOffset now, DateTimeOffset leaseExpiresAt, CancellationToken ct = default) => inner.ClaimRollbackAsync(workflowRunId, ownerId, now, leaseExpiresAt, ct);

    public ValueTask<bool> RenewRollbackLeaseAsync(Guid workflowRunId, string ownerId, DateTimeOffset leaseExpiresAt, CancellationToken ct = default) => inner.RenewRollbackLeaseAsync(workflowRunId, ownerId, leaseExpiresAt, ct);

    public ValueTask ReleaseRollbackLeaseAsync(Guid workflowRunId, string ownerId, DateTimeOffset now, CancellationToken ct = default) => inner.ReleaseRollbackLeaseAsync(workflowRunId, ownerId, now, ct);

    public ValueTask<bool> CompleteRollbackAsync(Guid workflowRunId, string ownerId, long generation, DateTimeOffset now, CancellationToken ct = default)
    {
        FaultBefore(BeforeRollbackTransition);
        return inner.CompleteRollbackAsync(workflowRunId, ownerId, generation, now, ct);
    }

    public ValueTask FailRollbackAsync(Guid workflowRunId, string ownerId, long generation, WorkflowError error, DateTimeOffset now, CancellationToken ct = default) => inner.FailRollbackAsync(workflowRunId, ownerId, generation, error, now, ct);

    public ValueTask<WorkflowStepCompensation?> ClaimCompensationAsync(Guid workflowRunId, string stepKey, string ownerId, long generation, DateTimeOffset now, DateTimeOffset leaseExpiresAt, string? actor, string? reason, CancellationToken ct = default) =>
        FaultAfter(AfterCompensationClaim, inner.ClaimCompensationAsync(workflowRunId, stepKey, ownerId, generation, now, leaseExpiresAt, actor, reason, ct));

    public ValueTask CompleteCompensationAsync(Guid compensationId, string ownerId, string? outputJson, DateTimeOffset now, CancellationToken ct = default)
    {
        FaultBefore(BeforeCompensationCommit);
        return inner.CompleteCompensationAsync(compensationId, ownerId, outputJson, now, ct);
    }

    public ValueTask FailCompensationAsync(Guid compensationId, string ownerId, WorkflowError error, DateTimeOffset? retryAt, DateTimeOffset now, CancellationToken ct = default) => inner.FailCompensationAsync(compensationId, ownerId, error, retryAt, now, ct);

    public ValueTask CreateOperationAsync(WorkflowRunOperation operation, CancellationToken ct = default) => inner.CreateOperationAsync(operation, ct);

    public ValueTask<long?> TryCreateAndClaimRollbackAndRestartAsync(WorkflowRunOperation operation, string ownerId, DateTimeOffset now, DateTimeOffset leaseExpiresAt, CancellationToken ct = default) => inner.TryCreateAndClaimRollbackAndRestartAsync(operation, ownerId, now, leaseExpiresAt, ct);

    public ValueTask<WorkflowRunOperation?> GetActiveOperationAsync(Guid workflowRunId, CancellationToken ct = default) => inner.GetActiveOperationAsync(workflowRunId, ct);

    public ValueTask<bool> UpdateOperationStatusAsync(Guid operationId, WorkflowOperationStatus status, DateTimeOffset now, CancellationToken ct = default) => inner.UpdateOperationStatusAsync(operationId, status, now, ct);

    public ValueTask<long?> ClaimRollbackAndRestartAsync(Guid workflowRunId, string ownerId, DateTimeOffset now, DateTimeOffset leaseExpiresAt, CancellationToken ct = default) => inner.ClaimRollbackAndRestartAsync(workflowRunId, ownerId, now, leaseExpiresAt, ct);

    public ValueTask<bool> RenewRollbackAndRestartLeaseAsync(Guid workflowRunId, string ownerId, DateTimeOffset leaseExpiresAt, CancellationToken ct = default) => inner.RenewRollbackAndRestartLeaseAsync(workflowRunId, ownerId, leaseExpiresAt, ct);

    public ValueTask ReleaseRollbackAndRestartLeaseAsync(Guid workflowRunId, string ownerId, DateTimeOffset now, CancellationToken ct = default) => inner.ReleaseRollbackAndRestartLeaseAsync(workflowRunId, ownerId, now, ct);

    public ValueTask<bool> CompleteRollbackAndRestartAsync(Guid workflowRunId, string ownerId, long generation, Guid operationId, IReadOnlyList<string> invalidateStepKeys, DateTimeOffset now, CancellationToken ct = default) => inner.CompleteRollbackAndRestartAsync(workflowRunId, ownerId, generation, operationId, invalidateStepKeys, now, ct);

    public ValueTask FailRollbackAndRestartAsync(Guid workflowRunId, string ownerId, long generation, Guid operationId, WorkflowError error, DateTimeOffset now, CancellationToken ct = default) => inner.FailRollbackAndRestartAsync(workflowRunId, ownerId, generation, operationId, error, now, ct);

    // ---- IWorkflowSignalRepository ----

    public ValueTask SendSignalAsync(Guid workflowRunId, string signalName, string? dataJson, CancellationToken ct = default) => inner.SendSignalAsync(workflowRunId, signalName, dataJson, ct);

    public ValueTask<SignalDelivery?> TryDeliverSignalAsync(Guid stepId, string ownerId, string signalName, DateTimeOffset now, CancellationToken ct = default) => inner.TryDeliverSignalAsync(stepId, ownerId, signalName, now, ct);

    public ValueTask<IReadOnlyList<WorkflowSignalRecord>> ListSignalsAsync(Guid workflowRunId, SignalQuery query, CancellationToken ct = default) => inner.ListSignalsAsync(workflowRunId, query, ct);

    public ValueTask<int> PurgeSignalsAsync(Guid workflowRunId, SignalPurgeOptions options, CancellationToken ct = default) => inner.PurgeSignalsAsync(workflowRunId, options, ct);

    // ---- IWorkflowTimerRepository ----

    public ValueTask ScheduleDelayAsync(Guid stepId, string ownerId, DateTimeOffset availableAt, DateTimeOffset now, CancellationToken ct = default) => inner.ScheduleDelayAsync(stepId, ownerId, availableAt, now, ct);

    public ValueTask CompleteDelayAsync(Guid stepId, DateTimeOffset now, CancellationToken ct = default) => inner.CompleteDelayAsync(stepId, now, ct);

    // ---- IWorkflowLeaseRepository ----

    public ValueTask<IReadOnlyList<Guid>> GetRunnableRunIdsAsync(DateTimeOffset now, int limit, CancellationToken ct = default) => inner.GetRunnableRunIdsAsync(now, limit, ct);

    public ValueTask<long?> TryClaimRunAsync(Guid workflowRunId, string ownerId, DateTimeOffset now, DateTimeOffset leaseExpiresAt, CancellationToken ct = default) => inner.TryClaimRunAsync(workflowRunId, ownerId, now, leaseExpiresAt, ct);

    public ValueTask<bool> RenewRunLeaseAsync(Guid workflowRunId, string ownerId, DateTimeOffset leaseExpiresAt, CancellationToken ct = default) => inner.RenewRunLeaseAsync(workflowRunId, ownerId, leaseExpiresAt, ct);

    public ValueTask ReleaseRunLeaseAsync(Guid workflowRunId, string ownerId, DateTimeOffset now, CancellationToken ct = default) => inner.ReleaseRunLeaseAsync(workflowRunId, ownerId, now, ct);

    public ValueTask<int> RecoverExpiredLeasesAsync(DateTimeOffset now, CancellationToken ct = default) => inner.RecoverExpiredLeasesAsync(now, ct);

    // ---- IWorkflowForkRepository ----

    public ValueTask<ForkPlan> PlanForkAsync(Guid sourceWorkflowRunId, string targetStepKey, StepRestartMode mode, CancellationToken ct = default) => inner.PlanForkAsync(sourceWorkflowRunId, targetStepKey, mode, ct);

    public ValueTask<ForkPlan> ForkRunAsync(Guid sourceWorkflowRunId, WorkflowRun newRun, string targetStepKey, StepRestartMode mode, string? actor, string? reason, CancellationToken ct = default) => inner.ForkRunAsync(sourceWorkflowRunId, newRun, targetStepKey, mode, actor, reason, ct);

    // ---- IWorkflowArtifactRepository ----

    public ValueTask<ArtifactPublicationResult> PublishArtifactAsync(ArtifactPublicationRequest request, CancellationToken ct = default) => inner.PublishArtifactAsync(request, ct);

    public ValueTask<WorkflowArtifactReference?> GetArtifactAsync(Guid artifactId, CancellationToken ct = default) => inner.GetArtifactAsync(artifactId, ct);

    public ValueTask<IReadOnlyList<WorkflowArtifactReference>> GetArtifactsAsync(Guid workflowRunId, CancellationToken ct = default) => inner.GetArtifactsAsync(workflowRunId, ct);

    public ValueTask<IReadOnlyList<WorkflowArtifactReference>> QueryArtifactsAsync(Guid workflowRunId, ArtifactQuery query, CancellationToken ct = default) => inner.QueryArtifactsAsync(workflowRunId, query, ct);

    public ValueTask<WorkflowArtifactReference?> GetLatestArtifactAsync(Guid workflowRunId, string name, CancellationToken ct = default) => inner.GetLatestArtifactAsync(workflowRunId, name, ct);
}
