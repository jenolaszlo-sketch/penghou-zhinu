using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Penghou.Zhinu.Execution.Outcomes;

/// <summary>
/// Runs a rollback to compensation for a run: claims the rollback lease,
/// compensates the planned steps, and settles the run to compensated or
/// failed.
/// </summary>
internal sealed class RollbackCoordinator
{
    private readonly IWorkflowStore store;
    private readonly ZhinuOptions options;
    private readonly JsonSerializerOptions serializerOptions;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<WorkflowEngine> logger;
    private readonly string ownerId;
    private readonly Action<Guid> notifyEventAppended;
    private readonly CompensationExecutor compensationExecutor;

    public RollbackCoordinator(
        IWorkflowStore store,
        ZhinuOptions options,
        JsonSerializerOptions serializerOptions,
        TimeProvider timeProvider,
        ILogger<WorkflowEngine> logger,
        string ownerId,
        Action<Guid> notifyEventAppended,
        CompensationExecutor compensationExecutor)
    {
        this.store = store;
        this.options = options;
        this.serializerOptions = serializerOptions;
        this.timeProvider = timeProvider;
        this.logger = logger;
        this.ownerId = ownerId;
        this.notifyEventAppended = notifyEventAppended;
        this.compensationExecutor = compensationExecutor;
    }

    public async Task RollbackAsync(
        Guid workflowRunId,
        string? targetStepKey,
        RollbackBoundary boundary,
        string? actor,
        string? reason,
        CancellationToken cancellationToken)
    {
        using var activity = ZhinuDiagnostics.StartActivity(
            ZhinuDiagnostics.Activities.RollbackExecute);
        activity?.SetTag(ZhinuDiagnostics.Attributes.WorkflowRunId, workflowRunId);
        activity?.SetTag(ZhinuDiagnostics.Attributes.OperationType, "rollback");
        var run = await store.GetRunAsync(
            workflowRunId,
            cancellationToken).ConfigureAwait(false) ??
            throw new WorkflowNotFoundException(
                $"Workflow '{workflowRunId:D}' does not exist.");
        if (run.Status == WorkflowStatus.Compensated)
            return;

        var plan = await store.PlanRollbackAsync(
            workflowRunId,
            targetStepKey,
            boundary,
            cancellationToken).ConfigureAwait(false);
        var compensateKeys = plan.Steps
            .Where(step => step.Action == RollbackAction.Compensate)
            .Select(step => step.StepKey)
            .ToList();

        var now = timeProvider.GetUtcNow();
        var generation = await store.ClaimRollbackAsync(
            workflowRunId,
            ownerId,
            now,
            now + options.LeaseDuration,
            cancellationToken).ConfigureAwait(false);
        if (generation is null)
            return;

        await using var renewal = new LeaseRenewal(
            timeProvider,
            options.LeaseRenewalInterval,
            token => store.RenewRollbackLeaseAsync(
                workflowRunId,
                ownerId,
                timeProvider.GetUtcNow() + options.LeaseDuration,
                token));
        try
        {
            await compensationExecutor.ExecuteAsync(
                run,
                compensateKeys,
                generation.Value,
                actor,
                reason,
                cancellationToken).ConfigureAwait(false);

            var now2 = timeProvider.GetUtcNow();
            var completed = await store.CompleteRollbackAsync(
                workflowRunId,
                ownerId,
                generation.Value,
                now2,
                CancellationToken.None).ConfigureAwait(false);
            if (!completed)
            {
                throw new WorkflowStateException(
                    "Rollback lost its run claim before the run could be marked compensated.");
            }
            await store.AppendEventAsync(
                workflowRunId,
                WorkflowEventTypes.WorkflowCompensated,
                JsonSerializer.Serialize(
                    new
                    {
                        actor,
                        reason,
                        compensatedSteps = compensateKeys.Count
                    },
                    serializerOptions),
                cancellationToken: CancellationToken.None).ConfigureAwait(false);
            notifyEventAppended(workflowRunId);
            logger.LogInformation(
                "Compensated workflow {WorkflowRunId} ({CompensatedCount} step(s)).",
                workflowRunId,
                compensateKeys.Count);
            ZhinuDiagnostics.RollbacksCompletedCounter.Add(1);
            activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Ok);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await store.ReleaseRollbackLeaseAsync(
                workflowRunId,
                ownerId,
                timeProvider.GetUtcNow(),
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception)
        {
            ZhinuDiagnostics.RecordException(activity, exception);
            try
            {
                await store.FailRollbackAsync(
                    workflowRunId,
                    ownerId,
                    generation.Value,
                    WorkflowError.FromException(
                        exception,
                        timeProvider.GetUtcNow()),
                    timeProvider.GetUtcNow(),
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception failureException)
            {
                logger.LogWarning(
                    failureException,
                    "Could not record rollback failure for workflow {WorkflowRunId}.",
                    workflowRunId);
            }
            logger.LogError(
                exception,
                "Rollback of workflow {WorkflowRunId} failed.",
                workflowRunId);
            throw;
        }
    }
}
