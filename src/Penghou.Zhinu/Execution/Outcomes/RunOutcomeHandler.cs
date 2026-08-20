using Microsoft.Extensions.Logging;

namespace Penghou.Zhinu.Execution.Outcomes;

/// <summary>
/// Settles a run after its workflow definition returns or throws: persists
/// completion, releases the lease on cancellation, or persists the failure.
/// </summary>
internal sealed class RunOutcomeHandler
{
    private readonly IWorkflowStore store;
    private readonly string ownerId;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<WorkflowEngine> logger;
    private readonly Action<Guid> notifyEventAppended;

    public RunOutcomeHandler(
        IWorkflowStore store,
        string ownerId,
        TimeProvider timeProvider,
        ILogger<WorkflowEngine> logger,
        Action<Guid> notifyEventAppended)
    {
        this.store = store;
        this.ownerId = ownerId;
        this.timeProvider = timeProvider;
        this.logger = logger;
        this.notifyEventAppended = notifyEventAppended;
    }

    public async Task CompleteAsync(
        Guid workflowRunId,
        string? outputJson,
        string outputType)
    {
        await store.CompleteRunAsync(
            workflowRunId,
            ownerId,
            outputJson,
            outputType,
            timeProvider.GetUtcNow(),
            CancellationToken.None).ConfigureAwait(false);
        ZhinuDiagnostics.RunsCompletedCounter.Add(1);
        notifyEventAppended(workflowRunId);
        logger.LogInformation(
            "Completed workflow {WorkflowRunId}.",
            workflowRunId);
    }

    public async Task ReleaseLeaseOnCancellationAsync(
        Guid workflowRunId)
    {
        var current = await store.GetRunAsync(
            workflowRunId,
            CancellationToken.None).ConfigureAwait(false);
        if (current?.Status != WorkflowStatus.Cancelled)
        {
            await store.ReleaseRunLeaseAsync(
                workflowRunId,
                ownerId,
                timeProvider.GetUtcNow(),
                CancellationToken.None).ConfigureAwait(false);
        }
    }

    public async Task FailOnExceptionAsync(
        Guid workflowRunId,
        Exception exception)
    {
        var current = await store.GetRunAsync(
            workflowRunId,
            CancellationToken.None).ConfigureAwait(false);
        if (current?.Status == WorkflowStatus.Running)
        {
            await store.FailRunAsync(
                workflowRunId,
                ownerId,
                WorkflowError.FromException(
                    exception,
                    timeProvider.GetUtcNow()),
                timeProvider.GetUtcNow(),
                CancellationToken.None).ConfigureAwait(false);
            ZhinuDiagnostics.RunsFailedCounter.Add(1);
            notifyEventAppended(workflowRunId);
        }
        logger.LogError(
            exception,
            "Workflow {WorkflowRunId} failed.",
            workflowRunId);
    }
}
