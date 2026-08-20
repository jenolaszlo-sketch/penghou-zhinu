using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Penghou.Zhinu.Execution.Outcomes;

/// <summary>
/// Drives a durable rollback-and-restart operation: claims the operation
/// lease, compensates and rewinds the run, then resets it for forward
/// execution. The operation is durable so a later resume completes it.
/// </summary>
internal sealed class RollbackAndRestartCoordinator
{
    private readonly IWorkflowStore store;
    private readonly ZhinuOptions options;
    private readonly JsonSerializerOptions serializerOptions;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<WorkflowEngine> logger;
    private readonly string ownerId;
    private readonly Action<Guid> notifyEventAppended;
    private readonly CompensationExecutor compensationExecutor;

    public RollbackAndRestartCoordinator(
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

    public async Task RollbackAndRestartAsync(
        Guid workflowRunId,
        string? actor,
        string? reason,
        CancellationToken cancellationToken)
    {
        var run = await store.GetRunAsync(
            workflowRunId,
            cancellationToken).ConfigureAwait(false) ??
            throw new KeyNotFoundException(
                $"Workflow '{workflowRunId:D}' does not exist.");
        if (run.Status == WorkflowStatus.Compensated)
            return;
        if (run.Status == WorkflowStatus.RollingBack)
            return;

        var operationId = Guid.NewGuid();
        var now = timeProvider.GetUtcNow();
        await store.CreateOperationAsync(
            new WorkflowRunOperation
            {
                OperationId = operationId,
                WorkflowRunId = workflowRunId,
                OperationType = "rollback-and-restart",
                Status = WorkflowOperationStatus.Requested,
                PayloadJson = JsonSerializer.Serialize(
                    new { actor, reason },
                    serializerOptions),
                CreatedAt = now,
                UpdatedAt = now,
            },
            cancellationToken).ConfigureAwait(false);

        var generation = await store.ClaimRollbackAndRestartAsync(
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
            token => store.RenewRollbackAndRestartLeaseAsync(
                workflowRunId,
                ownerId,
                timeProvider.GetUtcNow() + options.LeaseDuration,
                token));
        try
        {
            await ContinueAsync(
                workflowRunId,
                operationId,
                generation.Value,
                actor,
                reason,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await store.ReleaseRollbackAndRestartLeaseAsync(
                workflowRunId,
                ownerId,
                timeProvider.GetUtcNow(),
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception)
        {
            try
            {
                await store.FailRollbackAndRestartAsync(
                    workflowRunId,
                    ownerId,
                    generation.Value,
                    operationId,
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
                    "Could not record rollback-and-restart failure for workflow {WorkflowRunId}.",
                    workflowRunId);
            }
            logger.LogError(
                exception,
                "Rollback-and-restart of workflow {WorkflowRunId} failed.",
                workflowRunId);
            throw;
        }
    }

    public async Task ResumeAsync(
        Guid workflowRunId,
        CancellationToken cancellationToken)
    {
        var operation = await store.GetActiveOperationAsync(
            workflowRunId,
            cancellationToken).ConfigureAwait(false);
        if (operation is null)
            return;

        var now = timeProvider.GetUtcNow();
        var generation = await store.ClaimRollbackAndRestartAsync(
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
            token => store.RenewRollbackAndRestartLeaseAsync(
                workflowRunId,
                ownerId,
                timeProvider.GetUtcNow() + options.LeaseDuration,
                token));
        try
        {
            var payload = DeserializePayload(operation.PayloadJson);
            await ContinueAsync(
                workflowRunId,
                operation.OperationId,
                generation.Value,
                payload?.Actor,
                payload?.Reason,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await store.ReleaseRollbackAndRestartLeaseAsync(
                workflowRunId,
                ownerId,
                timeProvider.GetUtcNow(),
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception)
        {
            try
            {
                await store.FailRollbackAndRestartAsync(
                    workflowRunId,
                    ownerId,
                    generation.Value,
                    operation.OperationId,
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
                    "Could not record rollback-and-restart failure for workflow {WorkflowRunId}.",
                    workflowRunId);
            }
            logger.LogError(
                exception,
                "Resuming rollback-and-restart of workflow {WorkflowRunId} failed.",
                workflowRunId);
            throw;
        }
    }

    private async Task ContinueAsync(
        Guid workflowRunId,
        Guid operationId,
        long generation,
        string? actor,
        string? reason,
        CancellationToken cancellationToken)
    {
        var run = await store.GetRunAsync(
            workflowRunId,
            cancellationToken).ConfigureAwait(false) ??
            throw new KeyNotFoundException(
                $"Workflow '{workflowRunId:D}' does not exist.");
        var operation = await store.GetActiveOperationAsync(
            workflowRunId,
            cancellationToken).ConfigureAwait(false);
        if (operation is null || operation.OperationId != operationId)
            return;

        if (operation.Status == WorkflowOperationStatus.Requested ||
            operation.Status == WorkflowOperationStatus.Compensating)
        {
            var now = timeProvider.GetUtcNow();
            await store.UpdateOperationStatusAsync(
                operationId,
                WorkflowOperationStatus.Compensating,
                now,
                cancellationToken).ConfigureAwait(false);
            var plan = await store.PlanRollbackAsync(
                workflowRunId,
                null,
                RollbackBoundary.AfterStep,
                cancellationToken).ConfigureAwait(false);
            var compensateKeys = plan.Steps
                .Where(step => step.Action == RollbackAction.Compensate)
                .Select(step => step.StepKey)
                .ToList();
            await compensationExecutor.ExecuteAsync(
                run,
                compensateKeys,
                generation,
                actor,
                reason,
                cancellationToken).ConfigureAwait(false);
            await store.UpdateOperationStatusAsync(
                operationId,
                WorkflowOperationStatus.Rewinding,
                timeProvider.GetUtcNow(),
                cancellationToken).ConfigureAwait(false);
        }

        var steps = await store.GetStepsAsync(
            workflowRunId,
            cancellationToken).ConfigureAwait(false);
        var invalidateStepKeys = steps
            .Select(step => step.StepKey)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var now2 = timeProvider.GetUtcNow();
        await store.UpdateOperationStatusAsync(
            operationId,
            WorkflowOperationStatus.Restarting,
            now2,
            cancellationToken).ConfigureAwait(false);
        var completed = await store.CompleteRollbackAndRestartAsync(
            workflowRunId,
            ownerId,
            generation,
            operationId,
            invalidateStepKeys,
            now2,
            CancellationToken.None).ConfigureAwait(false);
        if (!completed)
        {
            throw new WorkflowStateException(
                "Rollback-and-restart lost its run claim before the run could be restarted.");
        }
        notifyEventAppended(workflowRunId);
        logger.LogInformation(
            "Rolled back and restarted workflow {WorkflowRunId} ({InvalidatedCount} step(s) rewound).",
            workflowRunId,
            invalidateStepKeys.Count);
    }

    private RollbackRestartPayload? DeserializePayload(
        string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
            return null;
        return JsonSerializer.Deserialize<RollbackRestartPayload>(
            payloadJson,
            serializerOptions);
    }

    private sealed record RollbackRestartPayload(string? Actor, string? Reason);
}
