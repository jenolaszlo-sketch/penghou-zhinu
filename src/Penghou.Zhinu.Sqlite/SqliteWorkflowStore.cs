using Penghou.Zhinu.Sqlite.Persistence;
using Penghou.Zhinu.Sqlite.Persistence.Leases;
using Penghou.Zhinu.Sqlite.Persistence.Signals;
using Penghou.Zhinu.Sqlite.Persistence.Steps;
using Penghou.Zhinu.Sqlite.Persistence.Timers;
using Penghou.Zhinu.Sqlite.Persistence.Workflows;
using Penghou.Zhinu.Sqlite.Persistence.Artifacts;
using Microsoft.Data.Sqlite;
using System.Diagnostics;

namespace Penghou.Zhinu.Sqlite;

/// <summary>
/// Implements transactional durable workflow state using SQLite. The facade
/// delegates every operation to a domain repository that coordinates the
/// persistence commands and queries.
/// </summary>
public sealed class SqliteWorkflowStore : IWorkflowStore
{
    private readonly IZhinuSqliteDatabase factory;
    private readonly SqliteWorkflowRepository workflows;
    private readonly SqliteStepRepository steps;
    private readonly SqliteSignalRepository signals;
    private readonly SqliteTimerRepository timers;
    private readonly SqliteLeaseRepository leases;
    private readonly SqliteArtifactRepository artifacts;
    private readonly bool detailedDiagnostics;

    public SqliteWorkflowStore(ZhinuSqliteOptions options)
        : this(new SqliteDatabase(options))
    {
    }

    /// <summary>
    /// Creates a store over a caller-supplied database owner so multiple
    /// components can share the same initialization gate and PRAGMAs for one
    /// database path.
    /// </summary>
    public SqliteWorkflowStore(IZhinuSqliteDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        factory = database;
        detailedDiagnostics = database.Options.EnableDetailedDiagnostics;
        workflows = new SqliteWorkflowRepository(factory);
        steps = new SqliteStepRepository(factory);
        signals = new SqliteSignalRepository(factory);
        timers = new SqliteTimerRepository(factory);
        leases = new SqliteLeaseRepository(factory);
        artifacts = new SqliteArtifactRepository(factory);
    }

    public ValueTask InitializeAsync(CancellationToken cancellationToken = default) =>
        ObserveAsync("initialize", () => workflows.InitializeAsync(cancellationToken));

    public ValueTask CreateRunAsync(
        WorkflowRun run,
        CancellationToken cancellationToken = default) =>
        ObserveAsync("run.create", () => workflows.CreateRunAsync(run, cancellationToken));

    public ValueTask<WorkflowRun?> GetRunAsync(
        Guid id,
        CancellationToken cancellationToken = default) =>
        ObserveAsync("run.get", () => workflows.GetRunAsync(id, cancellationToken));

    public ValueTask<IReadOnlyList<WorkflowRun>> GetRunsAsync(
        RunQuery query,
        CancellationToken cancellationToken = default) =>
        workflows.GetRunsAsync(query, cancellationToken);

    public ValueTask<WorkflowRun?> UpdateRunMetadataAsync(
        Guid workflowRunId,
        string? metadataJson,
        CancellationToken cancellationToken = default) =>
        workflows.UpdateRunMetadataAsync(workflowRunId, metadataJson, cancellationToken);

    public ValueTask<IReadOnlyList<WorkflowRun>> GetRunSubtreeAsync(
        Guid workflowRunId,
        int maxDepth,
        CancellationToken cancellationToken = default) =>
        workflows.GetRunSubtreeAsync(workflowRunId, maxDepth, cancellationToken);

    public ValueTask<IReadOnlyList<WorkflowEvent>> GetEventsAsync(
        Guid workflowRunId,
        long afterSequence,
        int limit,
        CancellationToken cancellationToken = default) =>
        workflows.GetEventsAsync(workflowRunId, afterSequence, limit, cancellationToken);

    public ValueTask<WorkflowEvent> AppendEventAsync(
        Guid workflowRunId,
        string eventType,
        string? dataJson,
        string? stepKey = null,
        int? attempt = null,
        CancellationToken cancellationToken = default) =>
        workflows.AppendEventAsync(
            workflowRunId,
            eventType,
            dataJson,
            stepKey,
            attempt,
            cancellationToken);

    public ValueTask CompleteRunAsync(
        Guid workflowRunId,
        string ownerId,
        string? outputJson,
        string outputType,
        DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        workflows.CompleteRunAsync(
            workflowRunId,
            ownerId,
            outputJson,
            outputType,
            now,
            cancellationToken);

    public ValueTask FailRunAsync(
        Guid workflowRunId,
        string ownerId,
        WorkflowError error,
        DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        workflows.FailRunAsync(workflowRunId, ownerId, error, now, cancellationToken);

    public ValueTask CancelRunAsync(
        Guid workflowRunId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        workflows.CancelRunAsync(workflowRunId, now, cancellationToken);

    public ValueTask<int> PurgeRunsAsync(
        DateTimeOffset olderThan,
        IReadOnlyList<WorkflowStatus>? statuses = null,
        CancellationToken cancellationToken = default) =>
        workflows.PurgeRunsAsync(olderThan, statuses, cancellationToken);

    public ValueTask<IReadOnlyList<WorkflowStepRun>> GetStepsAsync(
        Guid workflowRunId,
        CancellationToken cancellationToken = default) =>
        steps.GetStepsAsync(workflowRunId, cancellationToken);

    public ValueTask<ArtifactPublicationResult> PublishArtifactAsync(
        ArtifactPublicationRequest request,
        CancellationToken cancellationToken = default) =>
        ObserveAsync(
            "artifact.publish",
            () => artifacts.PublishArtifactAsync(request, cancellationToken));

    public ValueTask<WorkflowArtifactReference?> GetArtifactAsync(
        Guid artifactId,
        CancellationToken cancellationToken = default) =>
        artifacts.GetArtifactAsync(artifactId, cancellationToken);

    public ValueTask<IReadOnlyList<WorkflowArtifactReference>> GetArtifactsAsync(
        Guid workflowRunId,
        CancellationToken cancellationToken = default) =>
        artifacts.GetArtifactsAsync(workflowRunId, cancellationToken);

    public ValueTask<IReadOnlyList<WorkflowArtifactReference>> QueryArtifactsAsync(
        Guid workflowRunId,
        ArtifactQuery query,
        CancellationToken cancellationToken = default) =>
        artifacts.QueryArtifactsAsync(workflowRunId, query, cancellationToken);

    public ValueTask<WorkflowArtifactReference?> GetLatestArtifactAsync(
        Guid workflowRunId,
        string name,
        CancellationToken cancellationToken = default) =>
        artifacts.GetLatestArtifactAsync(workflowRunId, name, cancellationToken);

    public ValueTask<StepClaimResult> ClaimStepAsync(
        StepClaimRequest request,
        CancellationToken cancellationToken = default) =>
        ObserveAsync("step.claim", () => steps.ClaimStepAsync(request, cancellationToken));

    public ValueTask<bool> RenewStepLeaseAsync(
        Guid stepId,
        string ownerId,
        DateTimeOffset leaseExpiresAt,
        CancellationToken cancellationToken = default) =>
        steps.RenewStepLeaseAsync(stepId, ownerId, leaseExpiresAt, cancellationToken);

    public ValueTask CompleteStepAsync(
        Guid stepId,
        string ownerId,
        string? outputJson,
        DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        ObserveAsync(
            "step.complete",
            () => steps.CompleteStepAsync(
                stepId, ownerId, outputJson, now, cancellationToken));

    public ValueTask<IReadOnlyList<WorkflowEvent>> CompleteStepWithEventsAsync(
        Guid stepId,
        string ownerId,
        string? outputJson,
        DateTimeOffset now,
        IReadOnlyList<PendingWorkflowEvent>? events,
        CancellationToken cancellationToken = default) =>
        ObserveAsync(
            "step.complete",
            () => steps.CompleteStepWithEventsAsync(
                stepId, ownerId, outputJson, now, events, cancellationToken));

    public ValueTask FailStepAsync(
        Guid stepId,
        string ownerId,
        WorkflowError error,
        DateTimeOffset? retryAt,
        DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        steps.FailStepAsync(stepId, ownerId, error, retryAt, now, cancellationToken);

    public ValueTask<IReadOnlyList<StepDependency>> GetStepDependenciesAsync(
        Guid workflowRunId,
        CancellationToken cancellationToken = default) =>
        steps.GetStepDependenciesAsync(workflowRunId, cancellationToken);

    public ValueTask<IReadOnlyList<WorkflowStepCompensation>> GetCompensationsAsync(
        Guid workflowRunId,
        CancellationToken cancellationToken = default) =>
        steps.GetCompensationsAsync(workflowRunId, cancellationToken);

    public ValueTask<RestartPlan> PlanRestartAsync(
        Guid workflowRunId,
        string stepKey,
        StepRestartMode mode,
        CancellationToken cancellationToken = default) =>
        steps.PlanRestartAsync(workflowRunId, stepKey, mode, cancellationToken);

    public ValueTask<RestartPlan> RestartStepAsync(
        Guid workflowRunId,
        string stepKey,
        StepRestartMode mode,
        string? actor,
        string? reason,
        DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        steps.RestartStepAsync(
            workflowRunId,
            stepKey,
            mode,
            actor,
            reason,
            now,
            cancellationToken);

    public ValueTask<ForkPlan> PlanForkAsync(
        Guid sourceWorkflowRunId,
        string targetStepKey,
        StepRestartMode mode,
        CancellationToken cancellationToken = default) =>
        steps.PlanForkAsync(
            sourceWorkflowRunId,
            targetStepKey,
            mode,
            cancellationToken);

    public ValueTask<ForkPlan> ForkRunAsync(
        Guid sourceWorkflowRunId,
        WorkflowRun newRun,
        string targetStepKey,
        StepRestartMode mode,
        string? actor,
        string? reason,
        CancellationToken cancellationToken = default) =>
        steps.ForkRunAsync(
            sourceWorkflowRunId,
            newRun,
            targetStepKey,
            mode,
            actor,
            reason,
            cancellationToken);

    public ValueTask<RollbackPlan> PlanRollbackAsync(
        Guid workflowRunId,
        string? targetStepKey,
        RollbackBoundary boundary,
        CancellationToken cancellationToken = default) =>
        steps.PlanRollbackAsync(workflowRunId, targetStepKey, boundary, cancellationToken);

    public ValueTask<long?> ClaimRollbackAsync(
        Guid workflowRunId,
        string ownerId,
        DateTimeOffset now,
        DateTimeOffset leaseExpiresAt,
        CancellationToken cancellationToken = default) =>
        steps.ClaimRollbackAsync(
            workflowRunId,
            ownerId,
            now,
            leaseExpiresAt,
            cancellationToken);

    public ValueTask<bool> RenewRollbackLeaseAsync(
        Guid workflowRunId,
        string ownerId,
        DateTimeOffset leaseExpiresAt,
        CancellationToken cancellationToken = default) =>
        steps.RenewRollbackLeaseAsync(
            workflowRunId,
            ownerId,
            leaseExpiresAt,
            cancellationToken);

    public ValueTask ReleaseRollbackLeaseAsync(
        Guid workflowRunId,
        string ownerId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        steps.ReleaseRollbackLeaseAsync(workflowRunId, ownerId, now, cancellationToken);

    public ValueTask<bool> CompleteRollbackAsync(
        Guid workflowRunId,
        string ownerId,
        long generation,
        DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        steps.CompleteRollbackAsync(workflowRunId, ownerId, generation, now, cancellationToken);

    public ValueTask FailRollbackAsync(
        Guid workflowRunId,
        string ownerId,
        long generation,
        WorkflowError error,
        DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        steps.FailRollbackAsync(
            workflowRunId,
            ownerId,
            generation,
            error,
            now,
            cancellationToken);

    public ValueTask<WorkflowStepCompensation?> ClaimCompensationAsync(
        Guid workflowRunId,
        string stepKey,
        string ownerId,
        long generation,
        DateTimeOffset now,
        DateTimeOffset leaseExpiresAt,
        string? actor,
        string? reason,
        CancellationToken cancellationToken = default) =>
        steps.ClaimCompensationAsync(
            workflowRunId,
            stepKey,
            ownerId,
            generation,
            now,
            leaseExpiresAt,
            actor,
            reason,
            cancellationToken);

    public ValueTask CompleteCompensationAsync(
        Guid compensationId,
        string ownerId,
        string? outputJson,
        DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        steps.CompleteCompensationAsync(
            compensationId,
            ownerId,
            outputJson,
            now,
            cancellationToken);

    public ValueTask FailCompensationAsync(
        Guid compensationId,
        string ownerId,
        WorkflowError error,
        DateTimeOffset? retryAt,
        DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        steps.FailCompensationAsync(
            compensationId,
            ownerId,
            error,
            retryAt,
            now,
            cancellationToken);

    public ValueTask CreateOperationAsync(
        WorkflowRunOperation operation,
        CancellationToken cancellationToken = default) =>
        steps.CreateOperationAsync(operation, cancellationToken);

    public ValueTask<long?> TryCreateAndClaimRollbackAndRestartAsync(
        WorkflowRunOperation operation,
        string ownerId,
        DateTimeOffset now,
        DateTimeOffset leaseExpiresAt,
        CancellationToken cancellationToken = default) =>
        steps.TryCreateAndClaimRollbackAndRestartAsync(
            operation,
            ownerId,
            now,
            leaseExpiresAt,
            cancellationToken);

    public ValueTask<WorkflowRunOperation?> GetActiveOperationAsync(
        Guid workflowRunId,
        CancellationToken cancellationToken = default) =>
        steps.GetActiveOperationAsync(workflowRunId, cancellationToken);

    public ValueTask<bool> UpdateOperationStatusAsync(
        Guid operationId,
        WorkflowOperationStatus status,
        DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        steps.UpdateOperationStatusAsync(operationId, status, now, cancellationToken);

    public ValueTask<long?> ClaimRollbackAndRestartAsync(
        Guid workflowRunId,
        string ownerId,
        DateTimeOffset now,
        DateTimeOffset leaseExpiresAt,
        CancellationToken cancellationToken = default) =>
        steps.ClaimRollbackAndRestartAsync(
            workflowRunId,
            ownerId,
            now,
            leaseExpiresAt,
            cancellationToken);

    public ValueTask<bool> RenewRollbackAndRestartLeaseAsync(
        Guid workflowRunId,
        string ownerId,
        DateTimeOffset leaseExpiresAt,
        CancellationToken cancellationToken = default) =>
        steps.RenewRollbackAndRestartLeaseAsync(
            workflowRunId,
            ownerId,
            leaseExpiresAt,
            cancellationToken);

    public ValueTask ReleaseRollbackAndRestartLeaseAsync(
        Guid workflowRunId,
        string ownerId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        steps.ReleaseRollbackAndRestartLeaseAsync(
            workflowRunId,
            ownerId,
            now,
            cancellationToken);

    public ValueTask<bool> CompleteRollbackAndRestartAsync(
        Guid workflowRunId,
        string ownerId,
        long generation,
        Guid operationId,
        IReadOnlyList<string> invalidateStepKeys,
        DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        steps.CompleteRollbackAndRestartAsync(
            workflowRunId,
            ownerId,
            generation,
            operationId,
            invalidateStepKeys,
            now,
            cancellationToken);

    public ValueTask FailRollbackAndRestartAsync(
        Guid workflowRunId,
        string ownerId,
        long generation,
        Guid operationId,
        WorkflowError error,
        DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        steps.FailRollbackAndRestartAsync(
            workflowRunId,
            ownerId,
            generation,
            operationId,
            error,
            now,
            cancellationToken);

    public ValueTask SendSignalAsync(
        Guid workflowRunId,
        string signalName,
        string? dataJson,
        CancellationToken cancellationToken = default) =>
        signals.SendSignalAsync(workflowRunId, signalName, dataJson, cancellationToken);

    public ValueTask<SignalDelivery?> TryDeliverSignalAsync(
        Guid stepId,
        string ownerId,
        string signalName,
        DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        signals.TryDeliverSignalAsync(stepId, ownerId, signalName, now, cancellationToken);

    public ValueTask<IReadOnlyList<WorkflowSignalRecord>> ListSignalsAsync(
        Guid workflowRunId,
        SignalQuery query,
        CancellationToken cancellationToken = default) =>
        signals.ListSignalsAsync(workflowRunId, query, cancellationToken);

    public ValueTask<int> PurgeSignalsAsync(
        Guid workflowRunId,
        SignalPurgeOptions options,
        CancellationToken cancellationToken = default) =>
        signals.PurgeSignalsAsync(workflowRunId, options, cancellationToken);

    public ValueTask ScheduleDelayAsync(
        Guid stepId,
        string ownerId,
        DateTimeOffset availableAt,
        DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        timers.ScheduleDelayAsync(stepId, ownerId, availableAt, now, cancellationToken);

    public ValueTask CompleteDelayAsync(
        Guid stepId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        timers.CompleteDelayAsync(stepId, now, cancellationToken);

    public ValueTask<IReadOnlyList<Guid>> GetRunnableRunIdsAsync(
        DateTimeOffset now,
        int limit,
        CancellationToken cancellationToken = default) =>
        leases.GetRunnableRunIdsAsync(now, limit, cancellationToken);

    public ValueTask<long?> TryClaimRunAsync(
        Guid workflowRunId,
        string ownerId,
        DateTimeOffset now,
        DateTimeOffset leaseExpiresAt,
        CancellationToken cancellationToken = default) =>
        leases.TryClaimRunAsync(workflowRunId, ownerId, now, leaseExpiresAt, cancellationToken);

    public ValueTask<bool> RenewRunLeaseAsync(
        Guid workflowRunId,
        string ownerId,
        DateTimeOffset leaseExpiresAt,
        CancellationToken cancellationToken = default) =>
        leases.RenewRunLeaseAsync(workflowRunId, ownerId, leaseExpiresAt, cancellationToken);

    public ValueTask ReleaseRunLeaseAsync(
        Guid workflowRunId,
        string ownerId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        leases.ReleaseRunLeaseAsync(workflowRunId, ownerId, now, cancellationToken);

    public ValueTask<int> RecoverExpiredLeasesAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        ObserveAsync(
            "lease.recover",
            () => leases.RecoverExpiredLeasesAsync(now, cancellationToken));

    /// <summary>Truncates the WAL file via a checkpoint.</summary>
    public ValueTask CheckpointAsync(CancellationToken cancellationToken = default) =>
        factory.CheckpointAsync(cancellationToken);

    private async ValueTask ObserveAsync(
        string operation,
        Func<ValueTask> action)
    {
        using var activity = StartStoreActivity(operation);
        var started = Stopwatch.GetTimestamp();
        try
        {
            await action().ConfigureAwait(false);
            activity?.SetStatus(ActivityStatusCode.Ok);
        }
        catch (SqliteException exception)
        {
            RecordStoreFailure(activity, exception);
            throw;
        }
        finally
        {
            RecordStoreDuration(operation, started);
        }
    }

    private async ValueTask<T> ObserveAsync<T>(
        string operation,
        Func<ValueTask<T>> action)
    {
        using var activity = StartStoreActivity(operation);
        var started = Stopwatch.GetTimestamp();
        try
        {
            var result = await action().ConfigureAwait(false);
            activity?.SetStatus(ActivityStatusCode.Ok);
            return result;
        }
        catch (SqliteException exception)
        {
            RecordStoreFailure(activity, exception);
            throw;
        }
        finally
        {
            RecordStoreDuration(operation, started);
        }
    }

    private Activity? StartStoreActivity(string operation)
    {
        if (!detailedDiagnostics)
            return null;
        var activity = ZhinuSqliteDiagnostics.ActivitySource.StartActivity(
            ZhinuSqliteDiagnostics.StoreOperationActivity,
            ActivityKind.Client);
        activity?.SetTag(ZhinuSqliteDiagnostics.StoreOperationName, operation);
        return activity;
    }

    private static void RecordStoreFailure(
        Activity? activity,
        SqliteException exception)
    {
        ZhinuSqliteDiagnostics.Failures.Add(1);
        if (exception.SqliteErrorCode is 5 or 6)
            ZhinuSqliteDiagnostics.Busy.Add(1);
        activity?.SetStatus(ActivityStatusCode.Error);
    }

    private static void RecordStoreDuration(string operation, long started) =>
        ZhinuSqliteDiagnostics.OperationDuration.Record(
            Stopwatch.GetElapsedTime(started).TotalSeconds,
            new KeyValuePair<string, object?>(
                ZhinuSqliteDiagnostics.StoreOperationName,
                operation));
}
