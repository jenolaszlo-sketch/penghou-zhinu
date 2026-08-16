using System.Collections.Concurrent;
using System.Text.Json;

namespace Penghou.Zhinu;

/// <summary>
/// Executes explicit durable steps for one workflow run. After a restart the
/// workflow method is invoked again; committed steps return stored results
/// without invoking their delegates.
/// </summary>
public sealed class WorkflowContext
{
    private static readonly string DelayOutputType =
        SerializationIdentity.TypeId(typeof(DurableDelayMarker));
    private readonly IWorkflowStore store;
    private readonly string ownerId;
    private readonly ZhinuOptions options;
    private readonly JsonSerializerOptions serializerOptions;
    private readonly TimeProvider timeProvider;
    private readonly CancellationToken workflowCancellationToken;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> stepLocks =
        new(StringComparer.Ordinal);

    internal WorkflowContext(
        Guid workflowRunId,
        IWorkflowStore store,
        string ownerId,
        ZhinuOptions options,
        JsonSerializerOptions serializerOptions,
        TimeProvider timeProvider,
        CancellationToken workflowCancellationToken)
    {
        WorkflowRunId = workflowRunId;
        this.store = store;
        this.ownerId = ownerId;
        this.options = options;
        this.serializerOptions = serializerOptions;
        this.timeProvider = timeProvider;
        this.workflowCancellationToken = workflowCancellationToken;
    }

    public Guid WorkflowRunId { get; }

    /// <summary>Executes or reuses a durable step without an explicit input value.</summary>
    public Task<TOutput> StepAsync<TOutput>(
        string stepKey,
        Func<CancellationToken, Task<TOutput>> operation,
        RetryPolicy? retry = null,
        CancellationToken cancellationToken = default) =>
        StepAsync(
            stepKey,
            Unit.Value,
            (_, ct) => operation(ct),
            new StepOptions { Retry = retry ?? new RetryPolicy() },
            cancellationToken);

    /// <summary>Executes or reuses a durable step with retry and timeout options.</summary>
    public Task<TOutput> StepAsync<TOutput>(
        string stepKey,
        Func<WorkflowStepContext, CancellationToken, Task<TOutput>> operation,
        StepOptions? options = null,
        CancellationToken cancellationToken = default) =>
        StepAsync(
            stepKey,
            Unit.Value,
            (_, step, ct) => operation(step, ct),
            options,
            cancellationToken);

    /// <summary>
    /// Executes or reuses a durable step and verifies that repeated use of the
    /// key has the same serialized input and result type.
    /// </summary>
    public Task<TOutput> StepAsync<TInput, TOutput>(
        string stepKey,
        TInput input,
        Func<TInput, CancellationToken, Task<TOutput>> operation,
        StepOptions? options = null,
        CancellationToken cancellationToken = default) =>
        StepAsync(
            stepKey,
            input,
            (value, _, ct) => operation(value, ct),
            options,
            cancellationToken);

    /// <summary>
    /// Executes or reuses a typed durable step while exposing a stable
    /// downstream idempotency key for the current attempt.
    /// </summary>
    public async Task<TOutput> StepAsync<TInput, TOutput>(
        string stepKey,
        TInput input,
        Func<TInput, WorkflowStepContext, CancellationToken, Task<TOutput>> operation,
        StepOptions? stepOptions = null,
        CancellationToken cancellationToken = default)
    {
        ValidateStepKey(stepKey);
        ArgumentNullException.ThrowIfNull(operation);
        var configured = stepOptions ?? new StepOptions();
        configured.Validate();
        var inputJson = JsonSerializer.Serialize(input, serializerOptions);
        var inputType = SerializationIdentity.TypeId(typeof(TInput));
        var outputType = SerializationIdentity.TypeId(typeof(TOutput));
        var stepLock = stepLocks.GetOrAdd(stepKey, _ => new SemaphoreSlim(1, 1));
        using var linkedCancellation = CancellationTokenSource
            .CreateLinkedTokenSource(
                workflowCancellationToken,
                cancellationToken);
        await stepLock.WaitAsync(linkedCancellation.Token).ConfigureAwait(false);
        try
        {
            while (true)
            {
                var claim = await ClaimAsync(
                    stepKey,
                    inputJson,
                    inputType,
                    SerializationIdentity.Hash(inputJson),
                    outputType,
                    linkedCancellation.Token).ConfigureAwait(false);
                switch (claim.Disposition)
                {
                    case StepClaimDisposition.Reused:
                        return Deserialize<TOutput>(claim.Step.OutputJson, outputType);
                    case StepClaimDisposition.Waiting:
                        await WaitUntilAsync(
                            claim.Step.AvailableAt,
                            linkedCancellation.Token).ConfigureAwait(false);
                        continue;
                    case StepClaimDisposition.Busy:
                        await Task.Delay(
                            options.PollInterval,
                            timeProvider,
                            linkedCancellation.Token).ConfigureAwait(false);
                        continue;
                    case StepClaimDisposition.Failed:
                        throw new WorkflowStepFailedException(
                            stepKey,
                            claim.Step.Error ?? UnknownFailure(stepKey));
                    case StepClaimDisposition.Cancelled:
                        throw new OperationCanceledException(
                            $"Workflow step '{stepKey}' was cancelled.",
                            linkedCancellation.Token);
                    case StepClaimDisposition.Acquired:
                        return await ExecuteClaimedAsync(
                            claim.Step,
                            input,
                            operation,
                            configured,
                            outputType,
                            linkedCancellation.Token).ConfigureAwait(false);
                    default:
                        throw new WorkflowStateException(
                            $"Unknown claim result for step '{stepKey}'.");
                }
            }
        }
        finally
        {
            stepLock.Release();
        }
    }

    /// <summary>
    /// Persists a durable delay. Restarting the process does not reset its due time.
    /// </summary>
    public async Task DelayAsync(
        string stepKey,
        TimeSpan delay,
        CancellationToken cancellationToken = default)
    {
        ValidateStepKey(stepKey);
        if (delay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(delay));
        var inputJson = JsonSerializer.Serialize(delay.Ticks, serializerOptions);
        var stepLock = stepLocks.GetOrAdd(stepKey, _ => new SemaphoreSlim(1, 1));
        using var linkedCancellation = CancellationTokenSource
            .CreateLinkedTokenSource(
                workflowCancellationToken,
                cancellationToken);
        await stepLock.WaitAsync(linkedCancellation.Token).ConfigureAwait(false);
        try
        {
            while (true)
            {
                var claim = await ClaimAsync(
                    stepKey,
                    inputJson,
                    SerializationIdentity.TypeId(typeof(long)),
                    SerializationIdentity.Hash(inputJson),
                    DelayOutputType,
                    linkedCancellation.Token).ConfigureAwait(false);
                if (claim.Disposition == StepClaimDisposition.Reused)
                    return;
                if (claim.Disposition == StepClaimDisposition.Busy)
                {
                    await Task.Delay(
                        options.PollInterval,
                        timeProvider,
                        linkedCancellation.Token).ConfigureAwait(false);
                    continue;
                }
                if (claim.Disposition == StepClaimDisposition.Waiting)
                {
                    await WaitUntilAsync(
                        claim.Step.AvailableAt,
                        linkedCancellation.Token).ConfigureAwait(false);
                    await store.CompleteDelayAsync(
                        claim.Step.Id,
                        timeProvider.GetUtcNow(),
                        linkedCancellation.Token).ConfigureAwait(false);
                    return;
                }
                if (claim.Disposition == StepClaimDisposition.Cancelled)
                    throw new OperationCanceledException(linkedCancellation.Token);
                if (claim.Disposition == StepClaimDisposition.Failed)
                {
                    throw new WorkflowStepFailedException(
                        stepKey,
                        claim.Step.Error ?? UnknownFailure(stepKey));
                }

                var now = timeProvider.GetUtcNow();
                var availableAt = now + delay;
                await store.ScheduleDelayAsync(
                    claim.Step.Id,
                    ownerId,
                    availableAt,
                    now,
                    linkedCancellation.Token).ConfigureAwait(false);
                await WaitUntilAsync(
                    availableAt,
                    linkedCancellation.Token).ConfigureAwait(false);
                await store.CompleteDelayAsync(
                    claim.Step.Id,
                    timeProvider.GetUtcNow(),
                    linkedCancellation.Token).ConfigureAwait(false);
                return;
            }
        }
        finally
        {
            stepLock.Release();
        }
    }

    private async Task<TOutput> ExecuteClaimedAsync<TInput, TOutput>(
        WorkflowStepRun step,
        TInput input,
        Func<TInput, WorkflowStepContext, CancellationToken, Task<TOutput>> operation,
        StepOptions configured,
        string outputType,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            using var timeoutCancellation = configured.ExecutionTimeout is null
                ? null
                : new CancellationTokenSource(
                    configured.ExecutionTimeout.Value,
                    timeProvider);
            using var executionCancellation = timeoutCancellation is null
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                : CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    timeoutCancellation.Token);
            await using var renewal = new LeaseRenewal(
                timeProvider,
                options.LeaseRenewalInterval,
                token => store.RenewStepLeaseAsync(
                    step.Id,
                    ownerId,
                    timeProvider.GetUtcNow() + options.LeaseDuration,
                    token));
            try
            {
                var output = await operation(
                    input,
                    new WorkflowStepContext(
                        WorkflowRunId,
                        step.Id,
                        step.StepKey,
                        step.Attempt),
                    executionCancellation.Token).ConfigureAwait(false);
                var outputJson = JsonSerializer.Serialize(
                    output,
                    serializerOptions);
                await store.CompleteStepAsync(
                    step.Id,
                    ownerId,
                    outputJson,
                    timeProvider.GetUtcNow(),
                    cancellationToken).ConfigureAwait(false);
                return output;
            }
            catch (OperationCanceledException) when (
                timeoutCancellation?.IsCancellationRequested == true &&
                !cancellationToken.IsCancellationRequested)
            {
                var timeout = new TimeoutException(
                    $"Workflow step '{step.StepKey}' exceeded its execution timeout.");
                step = await RecordFailureAsync(
                    step,
                    timeout,
                    configured.Retry,
                    outputType,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                step = await RecordFailureAsync(
                    step,
                    exception,
                    configured.Retry,
                    outputType,
                    cancellationToken).ConfigureAwait(false);
            }

            if (step.Status == StepStatus.Failed)
            {
                throw new WorkflowStepFailedException(
                    step.StepKey,
                    step.Error ?? UnknownFailure(step.StepKey));
            }
            await WaitUntilAsync(
                step.AvailableAt,
                cancellationToken).ConfigureAwait(false);
            var claim = await ClaimAsync(
                step.StepKey,
                step.InputJson,
                step.InputType,
                step.InputHash,
                outputType,
                cancellationToken).ConfigureAwait(false);
            if (claim.Disposition != StepClaimDisposition.Acquired)
            {
                if (claim.Disposition == StepClaimDisposition.Reused)
                    return Deserialize<TOutput>(claim.Step.OutputJson, outputType);
                throw new WorkflowStateException(
                    $"Retry for step '{step.StepKey}' could not be acquired.");
            }
            step = claim.Step;
        }
    }

    private async Task<WorkflowStepRun> RecordFailureAsync(
        WorkflowStepRun step,
        Exception exception,
        RetryPolicy retry,
        string outputType,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var error = WorkflowError.FromException(exception, now, step.Attempt);
        var retryAt = step.Attempt < retry.MaxAttempts
            ? now + retry.DelayAfter(step.Attempt)
            : (DateTimeOffset?)null;
        await store.FailStepAsync(
            step.Id,
            ownerId,
            error,
            retryAt,
            now,
            cancellationToken).ConfigureAwait(false);
        return step with
        {
            Status = retryAt is null ? StepStatus.Failed : StepStatus.Waiting,
            AvailableAt = retryAt,
            Error = error,
            LeaseOwner = null,
            LeaseExpiresAt = null,
            OutputType = outputType
        };
    }

    private ValueTask<StepClaimResult> ClaimAsync(
        string stepKey,
        string? inputJson,
        string? inputType,
        string? inputHash,
        string outputType,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        return store.ClaimStepAsync(
            new StepClaimRequest
            {
                WorkflowRunId = WorkflowRunId,
                StepKey = stepKey,
                InputJson = inputJson,
                InputType = inputType,
                InputHash = inputHash,
                OutputType = outputType,
                OwnerId = ownerId,
                Now = now,
                LeaseExpiresAt = now + options.LeaseDuration
            },
            cancellationToken);
    }

    private async Task WaitUntilAsync(
        DateTimeOffset? availableAt,
        CancellationToken cancellationToken)
    {
        if (availableAt is null)
            throw new WorkflowStateException("Waiting step has no available time.");
        while (true)
        {
            var delay = availableAt.Value - timeProvider.GetUtcNow();
            if (delay <= TimeSpan.Zero)
                return;
            await Task.Delay(delay, timeProvider, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private T Deserialize<T>(string? json, string expectedType)
    {
        try
        {
            var value = JsonSerializer.Deserialize<T>(json ?? "null", serializerOptions);
            if (value is null && default(T) is not null)
            {
                throw new WorkflowSerializationException(
                    $"Stored step result '{expectedType}' was null.");
            }
            return value!;
        }
        catch (JsonException exception)
        {
            throw new WorkflowSerializationException(
                $"Stored step result could not be deserialized as '{expectedType}'.",
                exception);
        }
    }

    private static WorkflowError UnknownFailure(string stepKey) => new()
    {
        Type = typeof(WorkflowStateException).FullName!,
        Message = $"Step '{stepKey}' has no persisted failure details.",
        Timestamp = DateTimeOffset.MinValue
    };

    private static void ValidateStepKey(string stepKey) =>
        ArgumentException.ThrowIfNullOrWhiteSpace(stepKey);

    private readonly record struct Unit
    {
        public static Unit Value => default;
    }

    private readonly record struct DurableDelayMarker;
}
