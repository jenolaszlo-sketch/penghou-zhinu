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

    public IAsyncEnumerable<WorkflowEvent> SubscribeAsync(long afterSequence = 0,
        CancellationToken cancellationToken = default) =>
        engine.SubscribeAsync(WorkflowRunId, afterSequence, cancellationToken);
}
