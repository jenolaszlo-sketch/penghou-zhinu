namespace Penghou.Zhinu;

/// <summary>Capabilities used by execution hosts and schedulers.</summary>
public interface IWorkflowRuntime
{
    Task<Guid> StartAsync<TInput>(string workflowName, string workflowVersion,
        TInput input, Guid? workflowRunId = null, DateTimeOffset? deadline = null,
        object? metadata = null, CancellationToken cancellationToken = default);
    Task ExecuteAsync(Guid workflowRunId, CancellationToken cancellationToken = default);
    Task<int> RunAvailableAsync(CancellationToken cancellationToken = default);
}

/// <summary>Capabilities used by applications interacting with workflow runs.</summary>
public interface IWorkflowClient
{
    Task<WorkflowRun?> GetRunAsync(Guid workflowRunId, CancellationToken cancellationToken = default);
    Task<TOutput> WaitForCompletionAsync<TOutput>(Guid workflowRunId,
        DateTimeOffset? deadline = null, CancellationToken cancellationToken = default);
    Task SendSignalAsync(Guid workflowRunId, string signalName, object? data = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Optional application capability for retry-safe external signals.</summary>
public interface IIdempotentWorkflowClient
{
    Task<SignalSendReceipt> SendSignalWithReceiptAsync(
        Guid workflowRunId,
        string signalName,
        SignalSendOptions options,
        object? data = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Capabilities that mutate or administer existing workflow runs.</summary>
public interface IWorkflowAdministration
{
    Task CancelAsync(Guid workflowRunId, string? actor, string? reason,
        CancellationToken cancellationToken = default);
}

/// <summary>Capabilities for starting typed workflow runs.</summary>
public interface IWorkflowStarter
{
    Task<Guid> StartAsync<TInput>(string workflowName, string workflowVersion,
        TInput input, Guid? workflowRunId = null, DateTimeOffset? deadline = null,
        object? metadata = null, CancellationToken cancellationToken = default);
    Task<WorkflowHandle<TOutput>> StartHandleAsync<TInput, TOutput>(
        string workflowName, string workflowVersion, TInput input,
        Guid? workflowRunId = null, DateTimeOffset? deadline = null,
        object? metadata = null, CancellationToken cancellationToken = default);
    Task<TOutput> RunAsync<TInput, TOutput>(string workflowName, string workflowVersion,
        TInput input, Guid? workflowRunId = null, DateTimeOffset? deadline = null,
        object? metadata = null, CancellationToken cancellationToken = default);
}

/// <summary>Capabilities for querying run state, progress, and history.</summary>
public interface IWorkflowReader
{
    Task<WorkflowRun?> GetRunAsync(Guid workflowRunId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<WorkflowRun>> GetRunsAsync(RunQuery query,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<WorkflowStepRun>> GetStepsAsync(Guid workflowRunId,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<WorkflowEvent>> GetEventsAsync(Guid workflowRunId,
        long afterSequence = 0, int limit = 100, CancellationToken cancellationToken = default);
    Task<WorkflowRunProgress?> GetRunProgressAsync(Guid workflowRunId,
        RunProgressOptions? options = null, CancellationToken cancellationToken = default);
    Task<RunDiagnosis?> DiagnoseAsync(Guid workflowRunId,
        CancellationToken cancellationToken = default);
    Task<WorkflowResult<TOutput>> GetResultAsync<TOutput>(Guid workflowRunId,
        CancellationToken cancellationToken = default);
    Task<TOutput> WaitForCompletionAsync<TOutput>(Guid workflowRunId,
        DateTimeOffset? deadline = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<WorkflowArtifactReference>> GetArtifactsAsync(Guid workflowRunId,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<WorkflowArtifactReference>> QueryArtifactsAsync(Guid workflowRunId,
        ArtifactQuery query, CancellationToken cancellationToken = default);
    Task<WorkflowArtifactReference?> GetLatestArtifactAsync(Guid workflowRunId, string name,
        CancellationToken cancellationToken = default);
    Task<WorkflowArtifactReference?> GetArtifactAsync(Guid artifactId,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<StepDependency>> GetDependencyGraphAsync(Guid workflowRunId,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<WorkflowStepCompensation>> GetCompensationsAsync(Guid workflowRunId,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<WorkflowSignalRecord>> GetSignalsAsync(Guid workflowRunId,
        SignalQuery? query = null, CancellationToken cancellationToken = default);
    Task<WorkflowLoopProgress?> GetLoopProgressAsync(Guid workflowRunId,
        WorkflowLoopReference loop, CancellationToken cancellationToken = default);
}

/// <summary>Capabilities for intervening in existing runs: restart, fork, rollback, and retention.</summary>
public interface IWorkflowOperator : IWorkflowAdministration
{
    Task<BulkOperationResult> CancelManyAsync(RunQuery query, string? actor = null,
        string? reason = null, CancellationToken cancellationToken = default);
    Task<WorkflowRun?> UpdateRunMetadataAsync(Guid workflowRunId, object? metadata,
        CancellationToken cancellationToken = default);
    Task<int> PurgeRunsAsync(DateTimeOffset olderThan,
        IReadOnlyList<WorkflowStatus>? statuses = null, CancellationToken cancellationToken = default);
    Task<int> PurgeSignalsAsync(Guid workflowRunId, SignalPurgeOptions? options = null,
        CancellationToken cancellationToken = default);
    Task<RestartPlan> PlanRestartAsync(Guid workflowRunId, string stepKey,
        StepRestartMode mode = StepRestartMode.Dependents, CancellationToken cancellationToken = default);
    Task<RestartPlan> RestartStepAsync(Guid workflowRunId, string stepKey,
        RestartStepOptions options, CancellationToken cancellationToken = default);
    Task<RestartPlan> RestartStepAsync(Guid workflowRunId, string stepKey,
        CancellationToken cancellationToken = default);
    Task<RestartReceipt> RestartStepWithReceiptAsync(Guid workflowRunId, string stepKey,
        RestartStepOptions options, CancellationToken cancellationToken = default);
    Task<RestartPlan> PlanLoopRestartAsync(Guid workflowRunId, WorkflowLoopStepReference target,
        StepRestartMode mode = StepRestartMode.Dependents, CancellationToken cancellationToken = default);
    Task<RestartPlan> RestartLoopStepAsync(Guid workflowRunId, WorkflowLoopStepReference target,
        RestartStepOptions? options = null, CancellationToken cancellationToken = default);
    Task<RestartReceipt> RestartLoopStepWithReceiptAsync(Guid workflowRunId,
        WorkflowLoopStepReference target, RestartStepOptions options,
        CancellationToken cancellationToken = default);
    Task<ForkPlan> PlanForkAsync(Guid sourceWorkflowRunId, string targetStepKey,
        StepRestartMode mode = StepRestartMode.Dependents, CancellationToken cancellationToken = default);
    Task<Guid> ForkAsync(Guid sourceWorkflowRunId, string targetStepKey,
        ForkRunOptions? options = null, CancellationToken cancellationToken = default);
    Task<WorkflowHandle<TOutput>> ForkHandleAsync<TOutput>(Guid sourceWorkflowRunId,
        string targetStepKey, ForkRunOptions? options = null,
        CancellationToken cancellationToken = default);
    Task<RollbackPlan> PlanRollbackAsync(Guid workflowRunId,
        CancellationToken cancellationToken = default);
    Task<RollbackPlan> PlanRollbackAsync(Guid workflowRunId, string targetStepKey,
        RollbackOptions options, CancellationToken cancellationToken = default);
    Task<RollbackPlan> PlanRollbackAsync(Guid workflowRunId, string targetStepKey,
        CancellationToken cancellationToken = default);
    Task RollbackAsync(Guid workflowRunId, string? actor = null, string? reason = null,
        CancellationToken cancellationToken = default);
    Task RollbackToStepAsync(Guid workflowRunId, string stepKey, RollbackBoundary boundary,
        string? actor = null, string? reason = null, CancellationToken cancellationToken = default);
    Task RollbackAndRestartAsync(Guid workflowRunId, string? actor = null, string? reason = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Hosted execution capabilities: admission without awaiting plus drain.</summary>
public interface IHostedWorkflowRuntime : IWorkflowRuntime
{
    Task<int> AdmitAvailableAsync(CancellationToken cancellationToken = default);
    Task DrainAdmittedAsync(CancellationToken cancellationToken = default);
}
