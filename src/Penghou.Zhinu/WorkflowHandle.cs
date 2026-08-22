namespace Penghou.Zhinu;

/// <summary>A typed reference to a durable workflow run.</summary>
public sealed class WorkflowHandle<TOutput>
{
    private readonly WorkflowEngine engine;

    internal WorkflowHandle(WorkflowEngine engine, Guid workflowRunId)
    {
        this.engine = engine;
        WorkflowRunId = workflowRunId;
    }

    public Guid WorkflowRunId { get; }

    public Task<TOutput> WaitAsync(DateTimeOffset? deadline = null,
        CancellationToken cancellationToken = default) =>
        engine.WaitForCompletionAsync<TOutput>(WorkflowRunId, deadline, cancellationToken);

    public Task<WorkflowResult<TOutput>> GetResultAsync(
        CancellationToken cancellationToken = default) =>
        engine.GetResultAsync<TOutput>(WorkflowRunId, cancellationToken);

    public Task CancelAsync(CancellationToken cancellationToken = default) =>
        engine.CancelAsync(WorkflowRunId, cancellationToken);

    public Task CancelAsync(string? actor, string? reason,
        CancellationToken cancellationToken = default) =>
        engine.CancelAsync(WorkflowRunId, actor, reason, cancellationToken);

    public Task<WorkflowRun?> GetRunAsync(CancellationToken cancellationToken = default) =>
        engine.GetRunAsync(WorkflowRunId, cancellationToken);

    public Task<IReadOnlyList<WorkflowStepRun>> GetStepsAsync(
        CancellationToken cancellationToken = default) =>
        engine.GetStepsAsync(WorkflowRunId, cancellationToken);

    public Task<WorkflowRunProgress?> GetProgressAsync(
        RunProgressOptions? options = null,
        CancellationToken cancellationToken = default) =>
        engine.GetRunProgressAsync(WorkflowRunId, options, cancellationToken);

    public Task<RunDiagnosis?> DiagnoseAsync(CancellationToken cancellationToken = default) =>
        engine.DiagnoseAsync(WorkflowRunId, cancellationToken);

    public Task<IReadOnlyList<WorkflowEvent>> GetEventsAsync(
        long afterSequence = 0, int limit = 100,
        CancellationToken cancellationToken = default) =>
        engine.GetEventsAsync(WorkflowRunId, afterSequence, limit, cancellationToken);

    public Task<IReadOnlyList<WorkflowArtifactReference>> GetArtifactsAsync(
        CancellationToken cancellationToken = default) =>
        engine.GetArtifactsAsync(WorkflowRunId, cancellationToken);

    public Task<IReadOnlyList<WorkflowArtifactReference>> QueryArtifactsAsync(
        ArtifactQuery query, CancellationToken cancellationToken = default) =>
        engine.QueryArtifactsAsync(WorkflowRunId, query, cancellationToken);

    public Task<WorkflowArtifactReference?> GetLatestArtifactAsync(
        string name, CancellationToken cancellationToken = default) =>
        engine.GetLatestArtifactAsync(WorkflowRunId, name, cancellationToken);

    public Task SendSignalAsync(string signalName, object? data = null,
        CancellationToken cancellationToken = default) =>
        engine.SendSignalAsync(WorkflowRunId, signalName, data, cancellationToken);

    public Task SendSignalAsync<TPayload>(SignalDefinition<TPayload> signal, TPayload? data = default,
        CancellationToken cancellationToken = default) =>
        engine.SendSignalAsync(WorkflowRunId, signal, data, cancellationToken);

    public Task<WorkflowRun?> UpdateMetadataAsync(object? metadata,
        CancellationToken cancellationToken = default) =>
        engine.UpdateRunMetadataAsync(WorkflowRunId, metadata, cancellationToken);

    public IAsyncEnumerable<WorkflowEvent> SubscribeAsync(long afterSequence = 0,
        CancellationToken cancellationToken = default) =>
        engine.SubscribeAsync(WorkflowRunId, afterSequence, cancellationToken);
}
