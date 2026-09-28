using Penghou.Zhinu.Sqlite.Persistence;
using Penghou.Zhinu.Sqlite.Persistence.Leases;
using Penghou.Zhinu.Sqlite.Persistence.Signals;
using Penghou.Zhinu.Sqlite.Persistence.Steps;
using Penghou.Zhinu.Sqlite.Persistence.Timers;
using Penghou.Zhinu.Sqlite.Persistence.Workflows;
using Penghou.Zhinu.Sqlite.Persistence.Artifacts;
using Penghou.Zhinu.Sqlite.Persistence.Waits;
using Microsoft.Data.Sqlite;
using System.Diagnostics;

namespace Penghou.Zhinu.Sqlite;

/// <summary>
/// Implements transactional durable workflow state using SQLite. The facade
/// delegates every operation to a domain repository that coordinates the
/// persistence commands and queries.
/// </summary>
public sealed class SqliteWorkflowStore :
    IWorkflowStore,
    IIdempotentWorkflowRestartRepository,
    IIdempotentWorkflowSignalRepository,
    IAuditedWorkflowCancellationRepository,
    IWorkflowRetentionRepository,
    IWorkflowWaitRepository,
    IWorkflowEventExportRepository
{
    private readonly IZhinuSqliteDatabase factory;
    private readonly SqliteWorkflowRepository workflows;
    private readonly SqliteStepRepository steps;
    private readonly SqliteSignalRepository signals;
    private readonly SqliteTimerRepository timers;
    private readonly SqliteLeaseRepository leases;
    private readonly SqliteArtifactRepository artifacts;
    private readonly SqliteExternalOperationRepository externalOperations;
    private readonly SqliteWorkflowInstanceRepository instances;
    private readonly SqliteWaitRepository waits;
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
        externalOperations = new SqliteExternalOperationRepository(factory);
        instances = new SqliteWorkflowInstanceRepository(factory);
        waits = new SqliteWaitRepository(factory);
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
        ObserveAsync(
            "runs.get",
            () => workflows.GetRunsAsync(query, cancellationToken));

    public ValueTask<WorkflowRun?> UpdateRunMetadataAsync(
        Guid workflowRunId,
        string? metadataJson,
        CancellationToken cancellationToken = default) =>
        ObserveAsync(
            "run.update.metadata",
            () => workflows.UpdateRunMetadataAsync(workflowRunId, metadataJson, cancellationToken));

    public ValueTask<IReadOnlyList<WorkflowRun>> GetRunSubtreeAsync(
        Guid workflowRunId,
        int maxDepth,
        CancellationToken cancellationToken = default) =>
        ObserveAsync(
            "runs.subtree",
            () => workflows.GetRunSubtreeAsync(workflowRunId, maxDepth, cancellationToken));

    public ValueTask<IReadOnlyList<WorkflowEvent>> GetEventsAsync(
        Guid workflowRunId,
        long afterSequence,
        int limit,
        CancellationToken cancellationToken = default) =>
        ObserveAsync(
            "events.get",
            () => workflows.GetEventsAsync(workflowRunId, afterSequence, limit, cancellationToken));

    public ValueTask<WorkflowEvent> AppendEventAsync(
        Guid workflowRunId,
        string eventType,
        string? dataJson,
        string? stepKey = null,
        int? attempt = null,
        CancellationToken cancellationToken = default) =>
        ObserveAsync(
            "event.append",
            () => workflows.AppendEventAsync(
                workflowRunId,
                eventType,
                dataJson,
                stepKey,
                attempt,
                cancellationToken));

    public ValueTask CompleteRunAsync(
        Guid workflowRunId,
        string ownerId,
        string? outputJson,
        string outputType,
        DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        ObserveAsync(
            "run.complete",
            () => workflows.CompleteRunAsync(
                workflowRunId,
                ownerId,
                outputJson,
                outputType,
                now,
                cancellationToken));

    public ValueTask FailRunAsync(
        Guid workflowRunId,
        string ownerId,
        WorkflowError error,
        DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        ObserveAsync(
            "run.fail",
            () => workflows.FailRunAsync(workflowRunId, ownerId, error, now, cancellationToken));

    public ValueTask CancelRunAsync(
        Guid workflowRunId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        ObserveAsync(
            "run.cancel",
            () => workflows.CancelRunAsync(workflowRunId, now, cancellationToken));

    public ValueTask CancelRunAsync(
        Guid workflowRunId,
        string? actor,
        string? reason,
        DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        ObserveAsync(
            "run.cancel",
            () => workflows.CancelRunAsync(
                workflowRunId,
                actor,
                reason,
                now,
                cancellationToken));

    public ValueTask<int> PurgeRunsAsync(
        DateTimeOffset olderThan,
        IReadOnlyList<WorkflowStatus>? statuses = null,
        CancellationToken cancellationToken = default) =>
        ObserveAsync(
            "runs.purge",
            () => workflows.PurgeRunsAsync(olderThan, statuses, cancellationToken));

    public ValueTask<RunRetentionPreview> PreviewRetentionAsync(
        RunRetentionOptions options,
        CancellationToken cancellationToken = default) =>
        ObserveAsync(
            "runs.retention.preview",
            () => workflows.PreviewRetentionAsync(options, cancellationToken));

    public ValueTask<int> PurgeRetainedRunsAsync(
        RunRetentionOptions options,
        CancellationToken cancellationToken = default) =>
        ObserveAsync(
            "runs.retention.purge",
            () => workflows.PurgeRetainedRunsAsync(options, cancellationToken));

    public ValueTask<WorkflowWait> ParkWaitAsync(
        ParkWaitRequest request,
        CancellationToken cancellationToken = default) =>
        ObserveAsync(
            "waits.park",
            () => waits.ParkWaitAsync(request, cancellationToken));

    public ValueTask<WorkflowWait?> GetWaitAsync(
        Guid workflowRunId,
        string stepKey,
        CancellationToken cancellationToken = default) =>
        ObserveAsync(
            "waits.get",
            () => waits.GetWaitAsync(workflowRunId, stepKey, cancellationToken));

    public ValueTask<IReadOnlyList<WorkflowWait>> ListWaitsAsync(
        Guid workflowRunId,
        CancellationToken cancellationToken = default) =>
        ObserveAsync(
            "waits.list",
            () => waits.ListWaitsAsync(workflowRunId, cancellationToken));

    public ValueTask CompleteWaitAsync(
        Guid waitId,
        CancellationToken cancellationToken = default) =>
        ObserveAsync(
            "waits.complete",
            () => waits.CompleteWaitAsync(waitId, cancellationToken));

    public ValueTask MarkSignalWaitsReadyAsync(
        Guid workflowRunId,
        string signalName,
        DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        ObserveAsync(
            "waits.signal-ready",
            () => waits.MarkSignalWaitsReadyAsync(
                workflowRunId, signalName, now, cancellationToken));

    public ValueTask MarkChildWaitsReadyAsync(
        Guid childRunId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        ObserveAsync(
            "waits.child-ready",
            () => waits.MarkChildWaitsReadyAsync(childRunId, now, cancellationToken));

    public ValueTask<bool> HasParkedWaitsAsync(
        Guid workflowRunId,
        long leaseGeneration,
        CancellationToken cancellationToken = default) =>
        waits.HasParkedWaitsAsync(workflowRunId, leaseGeneration, cancellationToken);

    public ValueTask<IReadOnlyList<WorkflowEvent>> ReadExportBatchAsync(
        string consumerId,
        Guid workflowRunId,
        int limit,
        CancellationToken cancellationToken = default) =>
        ObserveAsync(
            "events.export.read",
            () => workflows.ReadExportBatchAsync(consumerId, workflowRunId, limit, cancellationToken));

    public ValueTask AcknowledgeExportAsync(
        string consumerId,
        Guid workflowRunId,
        long sequence,
        CancellationToken cancellationToken = default) =>
        ObserveAsync(
            "events.export.ack",
            () => workflows.AcknowledgeExportAsync(consumerId, workflowRunId, sequence, cancellationToken));

    public ValueTask<IReadOnlyList<WorkflowStepRun>> GetStepsAsync(
        Guid workflowRunId,
        CancellationToken cancellationToken = default) =>
        ObserveAsync(
            "steps.get",
            () => steps.GetStepsAsync(workflowRunId, cancellationToken));

    public ValueTask<ArtifactPublicationResult> PublishArtifactAsync(
        ArtifactPublicationRequest request,
        CancellationToken cancellationToken = default) =>
        ObserveAsync(
            "artifact.publish",
            () => artifacts.PublishArtifactAsync(request, cancellationToken));

    public ValueTask<WorkflowArtifactReference?> GetArtifactAsync(
        Guid artifactId,
        CancellationToken cancellationToken = default) =>
        ObserveAsync(
            "artifact.get",
            () => artifacts.GetArtifactAsync(artifactId, cancellationToken));

    public ValueTask<IReadOnlyList<WorkflowArtifactReference>> GetArtifactsAsync(
        Guid workflowRunId,
        CancellationToken cancellationToken = default) =>
        ObserveAsync(
            "artifacts.get",
            () => artifacts.GetArtifactsAsync(workflowRunId, cancellationToken));

    public ValueTask<IReadOnlyList<WorkflowArtifactReference>> QueryArtifactsAsync(
        Guid workflowRunId,
        ArtifactQuery query,
        CancellationToken cancellationToken = default) =>
        ObserveAsync(
            "artifacts.query",
            () => artifacts.QueryArtifactsAsync(workflowRunId, query, cancellationToken));

    public ValueTask<WorkflowArtifactReference?> GetLatestArtifactAsync(
        Guid workflowRunId,
        string name,
        CancellationToken cancellationToken = default) =>
        ObserveAsync(
            "artifacts.latest",
            () => artifacts.GetLatestArtifactAsync(workflowRunId, name, cancellationToken));

    public ValueTask<ArtifactInvalidation> InvalidateArtifactAsync(
        Guid artifactId,
        ArtifactInvalidationKind kind,
        string? reason,
        string? actor,
        DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        ObserveAsync(
            "artifacts.invalidate",
            () => artifacts.InvalidateArtifactAsync(
                artifactId, kind, reason, actor, now, cancellationToken));

    public ValueTask<IReadOnlyList<ArtifactInvalidation>> GetInvalidationsAsync(
        Guid artifactId,
        CancellationToken cancellationToken = default) =>
        ObserveAsync(
            "artifacts.invalidations",
            () => artifacts.GetInvalidationsAsync(artifactId, cancellationToken));

    public ValueTask<WorkflowExternalOperation> RegisterAsync(
        ExternalOperationRegistration request,
        CancellationToken cancellationToken = default) =>
        ObserveAsync(
            "external-operations.register",
            () => externalOperations.RegisterAsync(request, cancellationToken));

    public ValueTask<WorkflowExternalOperation?> GetAsync(
        Guid operationId,
        CancellationToken cancellationToken = default) =>
        ObserveAsync(
            "external-operations.get",
            () => externalOperations.GetAsync(operationId, cancellationToken));

    public ValueTask<IReadOnlyList<WorkflowExternalOperation>> ListAsync(
        Guid workflowRunId,
        int limit = 100,
        CancellationToken cancellationToken = default) =>
        ObserveAsync(
            "external-operations.list",
            () => externalOperations.ListAsync(workflowRunId, limit, cancellationToken));

    public ValueTask<WorkflowExternalOperation> AcquireAsync(
        Guid operationId,
        string ownerId,
        long leaseGeneration,
        CancellationToken cancellationToken = default) =>
        ObserveAsync(
            "external-operations.acquire",
            () => externalOperations.AcquireAsync(
                operationId, ownerId, leaseGeneration, cancellationToken));

    public ValueTask<WorkflowExternalOperation> CompleteAsync(
        Guid operationId,
        string ownerId,
        string? payloadJson,
        CancellationToken cancellationToken = default) =>
        ObserveAsync(
            "external-operations.complete",
            () => externalOperations.CompleteAsync(
                operationId, ownerId, payloadJson, cancellationToken));

    public ValueTask<WorkflowExternalOperation> FailAsync(
        Guid operationId,
        string ownerId,
        string? error,
        CancellationToken cancellationToken = default) =>
        ObserveAsync(
            "external-operations.fail",
            () => externalOperations.FailAsync(
                operationId, ownerId, error, cancellationToken));

    public ValueTask<WorkflowInstance> CreateInstanceAsync(
        string? metadataJson,
        CancellationToken cancellationToken = default) =>
        ObserveAsync(
            "instances.create",
            () => instances.CreateInstanceAsync(metadataJson, cancellationToken));

    public ValueTask<WorkflowInstance?> GetInstanceAsync(
        Guid instanceId,
        CancellationToken cancellationToken = default) =>
        ObserveAsync(
            "instances.get",
            () => instances.GetInstanceAsync(instanceId, cancellationToken));

    public ValueTask<WorkflowGeneration> CreateGenerationAsync(
        Guid instanceId,
        Guid workflowRunId,
        string? planRevision,
        string? executionFingerprint,
        Guid? predecessorGenerationId,
        CancellationToken cancellationToken = default) =>
        ObserveAsync(
            "generations.create",
            () => instances.CreateGenerationAsync(
                instanceId, workflowRunId, planRevision, executionFingerprint,
                predecessorGenerationId, cancellationToken));

    public ValueTask<WorkflowGeneration?> GetGenerationAsync(
        Guid generationId,
        CancellationToken cancellationToken = default) =>
        ObserveAsync(
            "generations.get",
            () => instances.GetGenerationAsync(generationId, cancellationToken));

    public ValueTask<WorkflowGeneration?> GetActiveGenerationAsync(
        Guid instanceId,
        CancellationToken cancellationToken = default) =>
        ObserveAsync(
            "generations.active",
            () => instances.GetActiveGenerationAsync(instanceId, cancellationToken));

    public ValueTask<IReadOnlyList<WorkflowGeneration>> ListGenerationsAsync(
        Guid instanceId,
        CancellationToken cancellationToken = default) =>
        ObserveAsync(
            "generations.list",
            () => instances.ListGenerationsAsync(instanceId, cancellationToken));

    public ValueTask<WorkflowGeneration?> GetGenerationByRunAsync(
        Guid workflowRunId,
        CancellationToken cancellationToken = default) =>
        ObserveAsync(
            "generations.by-run",
            () => instances.GetGenerationByRunAsync(workflowRunId, cancellationToken));

    public ValueTask<WorkflowGeneration> PrepareGenerationAsync(
        Guid generationId,
        CancellationToken cancellationToken = default) =>
        ObserveAsync(
            "generations.prepare",
            () => instances.PrepareGenerationAsync(generationId, cancellationToken));

    public ValueTask<WorkflowGeneration> ActivateGenerationAsync(
        Guid generationId,
        Guid? expectedPredecessorGenerationId,
        CancellationToken cancellationToken = default) =>
        ObserveAsync(
            "generations.activate",
            () => instances.ActivateGenerationAsync(
                generationId, expectedPredecessorGenerationId, cancellationToken));

    public ValueTask<WorkflowGeneration> PauseGenerationAsync(
        Guid generationId,
        CancellationToken cancellationToken = default) =>
        ObserveAsync(
            "generations.pause",
            () => instances.PauseGenerationAsync(generationId, cancellationToken));

    public ValueTask<WorkflowGeneration> ResumeGenerationAsync(
        Guid generationId,
        CancellationToken cancellationToken = default) =>
        ObserveAsync(
            "generations.resume",
            () => instances.ResumeGenerationAsync(generationId, cancellationToken));

    public ValueTask<WorkflowGeneration> RejectGenerationAsync(
        Guid generationId,
        CancellationToken cancellationToken = default) =>
        ObserveAsync(
            "generations.reject",
            () => instances.RejectGenerationAsync(generationId, cancellationToken));

    public ValueTask<GenerationDisposition> RecordDispositionAsync(
        Guid generationId,
        CheckpointDisposition disposition,
        string? reason,
        string? actor,
        CancellationToken cancellationToken = default) =>
        ObserveAsync(
            "generations.disposition.record",
            () => instances.RecordDispositionAsync(
                generationId, disposition, reason, actor, cancellationToken));

    public ValueTask<IReadOnlyList<GenerationDisposition>> ListDispositionsAsync(
        Guid generationId,
        CancellationToken cancellationToken = default) =>
        ObserveAsync(
            "generations.disposition.list",
            () => instances.ListDispositionsAsync(generationId, cancellationToken));

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
        ObserveAsync(
            "steps.dependencies",
            () => steps.GetStepDependenciesAsync(workflowRunId, cancellationToken));

    public ValueTask<IReadOnlyList<WorkflowStepCompensation>> GetCompensationsAsync(
        Guid workflowRunId,
        CancellationToken cancellationToken = default) =>
        ObserveAsync(
            "steps.compensations",
            () => steps.GetCompensationsAsync(workflowRunId, cancellationToken));

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

    public ValueTask<RestartReceipt> RestartStepIdempotentlyAsync(
        Guid workflowRunId,
        string stepKey,
        StepRestartMode mode,
        Guid operationId,
        string? actor,
        string? reason,
        DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        ObserveAsync(
            "step.restart.idempotent",
            () => steps.RestartStepIdempotentlyAsync(
                workflowRunId,
                stepKey,
                mode,
                operationId,
                actor,
                reason,
                now,
                cancellationToken));

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
        ObserveAsync(
            "operations.active",
            () => steps.GetActiveOperationAsync(workflowRunId, cancellationToken));

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
        ObserveAsync(
            "signal.send",
            () => signals.SendSignalAsync(workflowRunId, signalName, dataJson, cancellationToken));

    public ValueTask<SignalSendReceipt> SendSignalIdempotentlyAsync(
        Guid workflowRunId,
        string signalName,
        string? dataJson,
        Guid signalId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        ObserveAsync(
            "signal.send.idempotent",
            () => signals.SendSignalIdempotentlyAsync(
                workflowRunId,
                signalName,
                dataJson,
                signalId,
                now,
                cancellationToken));

    public ValueTask<SignalDelivery?> TryDeliverSignalAsync(
        Guid stepId,
        string ownerId,
        long leaseGeneration,
        string signalName,
        DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        signals.TryDeliverSignalAsync(stepId, ownerId, leaseGeneration, signalName, now, cancellationToken);

    public ValueTask<IReadOnlyList<WorkflowSignalRecord>> ListSignalsAsync(
        Guid workflowRunId,
        SignalQuery query,
        CancellationToken cancellationToken = default) =>
        ObserveAsync(
            "signals.list",
            () => signals.ListSignalsAsync(workflowRunId, query, cancellationToken));

    public ValueTask<int> PurgeSignalsAsync(
        Guid workflowRunId,
        SignalPurgeOptions options,
        CancellationToken cancellationToken = default) =>
        ObserveAsync(
            "signals.purge",
            () => signals.PurgeSignalsAsync(workflowRunId, options, cancellationToken));

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

    /// <summary>
    /// Safe readiness probe: opens the database, verifies schema compatibility,
    /// and runs a trivial read. Never claims a step or mutates state.
    /// </summary>
    public async ValueTask<WorkflowStoreHealth> CheckHealthAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            await factory.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            await using var connection = await factory.OpenAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var command = SqliteStoreSupport.CreateCommand(
                connection, null, "SELECT 1;");
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return new WorkflowStoreHealth
            {
                IsHealthy = true,
                StoreName = "sqlite",
                SchemaVersion = ZhinuSqliteSchema.CurrentVersion,
                WalMode = factory.Options.EnableWal
            };
        }
        catch (Exception exception)
        {
            return new WorkflowStoreHealth
            {
                IsHealthy = false,
                StoreName = "sqlite",
                Detail = exception.Message
            };
        }
    }

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
            throw new WorkflowPersistenceException(
                $"SQLite operation '{operation}' failed.",
                exception);
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
            throw new WorkflowPersistenceException(
                $"SQLite operation '{operation}' failed.",
                exception);
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

