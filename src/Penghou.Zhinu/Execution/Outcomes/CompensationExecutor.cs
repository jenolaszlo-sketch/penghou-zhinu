using System.Text.Json;

namespace Penghou.Zhinu.Execution.Outcomes;

/// <summary>
/// Executes durable compensations for a rollback: replays the workflow to
/// reconstruct compensation invocations, then claims and runs each one with
/// retry, timeout, and failure accounting.
/// </summary>
internal sealed class CompensationExecutor
{
    private readonly IWorkflowStore store;
    private readonly IWorkflowRegistry registry;
    private readonly ZhinuOptions options;
    private readonly JsonSerializerOptions serializerOptions;
    private readonly TimeProvider timeProvider;
    private readonly IWorkflowEventPublisher? eventPublisher;
    private readonly string ownerId;
    private readonly Action<Guid> notifyEventAppended;
    private readonly IWorkflowStepResolver? workflowStepResolver;

    public CompensationExecutor(
        IWorkflowStore store,
        IWorkflowRegistry registry,
        ZhinuOptions options,
        JsonSerializerOptions serializerOptions,
        TimeProvider timeProvider,
        IWorkflowEventPublisher? eventPublisher,
        string ownerId,
        Action<Guid> notifyEventAppended,
        IWorkflowStepResolver? workflowStepResolver)
    {
        this.store = store;
        this.registry = registry;
        this.options = options;
        this.serializerOptions = serializerOptions;
        this.timeProvider = timeProvider;
        this.eventPublisher = eventPublisher;
        this.ownerId = ownerId;
        this.notifyEventAppended = notifyEventAppended;
        this.workflowStepResolver = workflowStepResolver;
    }

    public async Task ExecuteAsync(
        WorkflowRun run,
        IReadOnlyList<string> compensateKeys,
        long generation,
        string? actor,
        string? reason,
        CancellationToken cancellationToken)
    {
        if (compensateKeys.Count == 0)
            return;
        var steps = (await store.GetStepsAsync(
            run.Id,
            cancellationToken).ConfigureAwait(false))
            .ToDictionary(
                step => step.StepKey,
                StringComparer.Ordinal);
        var rows = await store.GetCompensationsAsync(
            run.Id,
            cancellationToken).ConfigureAwait(false);
        var byKey = new Dictionary<string, WorkflowStepCompensation>(
            StringComparer.Ordinal);
        foreach (var key in compensateKeys)
        {
            var row = rows
                .Where(item =>
                    string.Equals(item.StepKey, key, StringComparison.Ordinal) &&
                    item.InputJson is not null &&
                    item.Status is CompensationStatus.Pending or
                        CompensationStatus.Failed)
                .OrderByDescending(item => item.Revision)
                .FirstOrDefault();
            if (row is not null)
                byKey[key] = row;
        }
        if (byKey.Count == 0)
            return;
        if (!registry.TryGet(
                run.WorkflowName,
                run.WorkflowVersion,
                out var registration))
        {
            throw new WorkflowDefinitionUnavailableException(
                run.WorkflowName,
                run.WorkflowVersion);
        }
        var context = new WorkflowContext(
            run.Id,
            store,
            ownerId,
            options,
            serializerOptions,
            timeProvider,
            generation,
            cancellationToken,
            eventPublisher,
            executeChildRun: null,
            replaySteps: steps,
            rollbackCompensations: byKey,
            onEventAppended: notifyEventAppended,
            workflowStepResolver: workflowStepResolver);
        await registration!.ExecuteAsync(
            context,
            run.InputJson ?? "null",
            serializerOptions,
            cancellationToken).ConfigureAwait(false);
        var invocations = context.RollbackInvocations;
        foreach (var key in compensateKeys)
        {
            var invocation = invocations.FirstOrDefault(item =>
                string.Equals(
                    item.StepKey,
                    key,
                    StringComparison.Ordinal));
            if (invocation is null)
                continue;
            await ExecuteOneAsync(
                invocation,
                generation,
                actor,
                reason,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ExecuteOneAsync(
        WorkflowContext.CompensationInvocation invocation,
        long generation,
        string? actor,
        string? reason,
        CancellationToken cancellationToken)
    {
        var compensation = invocation.Compensation;
        using var activity = ZhinuDiagnostics.StartActivity(
            ZhinuDiagnostics.Activities.CompensationExecute);
        activity?.SetTag(
            ZhinuDiagnostics.Attributes.WorkflowRunId,
            compensation.WorkflowRunId);
        activity?.SetTag(ZhinuDiagnostics.Attributes.StepKey, invocation.StepKey);
        var retryPolicy = DeserializeRetryPolicy(compensation.RetryPolicyJson);
        while (true)
        {
            var now = timeProvider.GetUtcNow();
            var claim = await store.ClaimCompensationAsync(
                compensation.WorkflowRunId,
                invocation.StepKey,
                ownerId,
                generation,
                now,
                now + options.LeaseDuration,
                actor,
                reason,
                cancellationToken).ConfigureAwait(false);
            if (claim is null)
                return;
            activity?.SetTag(ZhinuDiagnostics.Attributes.StepAttempt, claim.Attempt);
            activity?.SetTag(ZhinuDiagnostics.Attributes.StepRevision, claim.Revision);

            await store.AppendEventAsync(
                claim.WorkflowRunId,
                WorkflowEventTypes.CompensationStarted,
                null,
                claim.StepKey,
                claim.Attempt,
                cancellationToken).ConfigureAwait(false);
            notifyEventAppended(claim.WorkflowRunId);

            using var timeoutCancellation = compensation.ExecutionTimeout is { } executionTimeout
                ? new CancellationTokenSource(executionTimeout, timeProvider)
                : null;
            using var executionCancellation = timeoutCancellation is null
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                : CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    timeoutCancellation.Token);
            try
            {
                await invocation.Execute(executionCancellation.Token)
                    .ConfigureAwait(false);
                await store.CompleteCompensationAsync(
                    claim.Id,
                    ownerId,
                    null,
                    timeProvider.GetUtcNow(),
                    cancellationToken).ConfigureAwait(false);
                await store.AppendEventAsync(
                    claim.WorkflowRunId,
                    WorkflowEventTypes.CompensationCompleted,
                    null,
                    claim.StepKey,
                    claim.Attempt,
                    cancellationToken).ConfigureAwait(false);
                notifyEventAppended(claim.WorkflowRunId);
                ZhinuDiagnostics.CompensationsExecutedCounter.Add(1);
                activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Ok);
                return;
            }
            catch (OperationCanceledException) when (
                timeoutCancellation?.IsCancellationRequested == true &&
                !cancellationToken.IsCancellationRequested)
            {
                var timeout = new WorkflowTimeoutException(
                    $"Compensation for step '{claim.StepKey}' exceeded its execution timeout.");
                var retryAt = ScheduleRetry(claim, retryPolicy);
                await RecordFailureAsync(
                    claim,
                    timeout,
                    retryAt).ConfigureAwait(false);
                if (retryAt is null)
                {
                    throw new RollbackFailedException(
                        $"Compensation for step '{claim.StepKey}' failed permanently after {claim.Attempt} attempt(s).",
                        timeout);
                }
                await WaitUntilAsync(retryAt.Value, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                var retryAt = ScheduleRetry(claim, retryPolicy);
                await RecordFailureAsync(
                    claim,
                    exception,
                    retryAt).ConfigureAwait(false);
                if (retryAt is null)
                {
                    throw new RollbackFailedException(
                        $"Compensation for step '{claim.StepKey}' failed permanently after {claim.Attempt} attempt(s).",
                        exception);
                }
                await WaitUntilAsync(retryAt.Value, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private DateTimeOffset? ScheduleRetry(
        WorkflowStepCompensation claim,
        RetryPolicy retryPolicy) =>
        claim.Attempt < retryPolicy.MaxAttempts
            ? timeProvider.GetUtcNow() + retryPolicy.DelayAfter(claim.Attempt)
            : (DateTimeOffset?)null;

    private async Task RecordFailureAsync(
        WorkflowStepCompensation claim,
        Exception exception,
        DateTimeOffset? retryAt)
    {
        var now = timeProvider.GetUtcNow();
        var error = WorkflowError.FromException(exception, now, claim.Attempt);
        if (retryAt is null)
            ZhinuDiagnostics.CompensationsFailedCounter.Add(1);
        await store.FailCompensationAsync(
            claim.Id,
            ownerId,
            error,
            retryAt,
            now,
            CancellationToken.None).ConfigureAwait(false);
        await store.AppendEventAsync(
                claim.WorkflowRunId,
                WorkflowEventTypes.CompensationFailed,
                JsonSerializer.Serialize(error, serializerOptions),
                claim.StepKey,
                claim.Attempt,
                CancellationToken.None).ConfigureAwait(false);
        notifyEventAppended(claim.WorkflowRunId);
    }

    private RetryPolicy DeserializeRetryPolicy(string? retryPolicyJson)
    {
        if (retryPolicyJson is null)
            return new RetryPolicy();
        try
        {
            return JsonSerializer.Deserialize<RetryPolicy>(
                    retryPolicyJson,
                    serializerOptions) ?? new RetryPolicy();
        }
        catch (JsonException)
        {
            return new RetryPolicy();
        }
    }

    private async Task WaitUntilAsync(
        DateTimeOffset availableAt,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var delay = availableAt - timeProvider.GetUtcNow();
            if (delay <= TimeSpan.Zero)
                return;
            await Task.Delay(delay, timeProvider, cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
