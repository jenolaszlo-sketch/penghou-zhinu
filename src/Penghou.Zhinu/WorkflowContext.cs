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
    private readonly long leaseGeneration;
    private readonly CancellationToken workflowCancellationToken;
    private readonly IWorkflowEventPublisher? eventPublisher;
    private readonly Func<Guid, CancellationToken, Task>? executeChildRun;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> stepLocks =
        new(StringComparer.Ordinal);
    private readonly List<string> currentDependencies = [];

    internal WorkflowContext(
        Guid workflowRunId,
        IWorkflowStore store,
        string ownerId,
        ZhinuOptions options,
        JsonSerializerOptions serializerOptions,
        TimeProvider timeProvider,
        long leaseGeneration,
        CancellationToken workflowCancellationToken,
        IWorkflowEventPublisher? eventPublisher = null,
        Func<Guid, CancellationToken, Task>? executeChildRun = null)
    {
        WorkflowRunId = workflowRunId;
        this.store = store;
        this.ownerId = ownerId;
        this.options = options;
        this.serializerOptions = serializerOptions;
        this.timeProvider = timeProvider;
        this.leaseGeneration = leaseGeneration;
        this.workflowCancellationToken = workflowCancellationToken;
        this.eventPublisher = eventPublisher;
        this.executeChildRun = executeChildRun;
    }

    public Guid WorkflowRunId { get; }

    /// <summary>
    /// Declares durable dependencies for every step created until the returned
    /// scope is disposed. Nested scopes combine their dependencies. The scope
    /// is a runtime helper only: it adds no storage writes of its own, and the
    /// recorded edges are committed transactionally with each step claim.
    /// </summary>
    public IDisposable DependsOn(params string[] stepKeys)
    {
        foreach (var stepKey in stepKeys)
            ValidateStepKey(stepKey);
        var added = stepKeys
            .Where(stepKey => !currentDependencies.Contains(stepKey))
            .ToList();
        currentDependencies.AddRange(added);
        return new DependencyScope(this, added);
    }

    private sealed class DependencyScope : IDisposable
    {
        private readonly WorkflowContext owner;
        private readonly IReadOnlyList<string> added;
        private bool disposed;

        public DependencyScope(WorkflowContext owner, IReadOnlyList<string> added)
        {
            this.owner = owner;
            this.added = added;
        }

        public void Dispose()
        {
            if (disposed)
                return;
            disposed = true;
            foreach (var stepKey in added)
                owner.currentDependencies.Remove(stepKey);
        }
    }

    private IReadOnlyCollection<string>? ResolveDependencies(StepOptions? options)
    {
        var explicitKeys = options?.DependsOn;
        if (currentDependencies.Count == 0)
            return explicitKeys is { Count: > 0 } ? explicitKeys : null;
        if (explicitKeys is null or { Count: 0 })
            return currentDependencies.ToArray();
        return explicitKeys
            .Concat(currentDependencies)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>Executes or reuses a durable step without an explicit input value.</summary>
    public Task<TOutput> StepAsync<TOutput>(
        string stepKey,
        Func<CancellationToken, Task<TOutput>> operation,
        RetryPolicy? retry = null,
        CancellationToken cancellationToken = default,
        Func<TOutput, WorkflowStepContext, CancellationToken, Task>? compensation = null) =>
        StepAsync(
            stepKey,
            Unit.Value,
            (_, ct) => operation(ct),
            new StepOptions { Retry = retry ?? new RetryPolicy() },
            cancellationToken,
            compensation);

    /// <summary>Executes or reuses a durable step with retry and timeout options.</summary>
    public Task<TOutput> StepAsync<TOutput>(
        string stepKey,
        Func<WorkflowStepContext, CancellationToken, Task<TOutput>> operation,
        StepOptions? options = null,
        CancellationToken cancellationToken = default,
        Func<TOutput, WorkflowStepContext, CancellationToken, Task>? compensation = null) =>
        StepAsync(
            stepKey,
            Unit.Value,
            (_, step, ct) => operation(step, ct),
            options,
            cancellationToken,
            compensation);

    /// <summary>
    /// Executes or reuses a durable step and verifies that repeated use of the
    /// key has the same serialized input and result type.
    /// </summary>
    public Task<TOutput> StepAsync<TInput, TOutput>(
        string stepKey,
        TInput input,
        Func<TInput, CancellationToken, Task<TOutput>> operation,
        StepOptions? options = null,
        CancellationToken cancellationToken = default,
        Func<TOutput, WorkflowStepContext, CancellationToken, Task>? compensation = null) =>
        StepAsync(
            stepKey,
            input,
            (value, _, ct) => operation(value, ct),
            options,
            cancellationToken,
            compensation);

    /// <summary>
    /// Executes or reuses a typed durable step while exposing a stable
    /// downstream idempotency key for the current attempt.
    /// </summary>
    public async Task<TOutput> StepAsync<TInput, TOutput>(
        string stepKey,
        TInput input,
        Func<TInput, WorkflowStepContext, CancellationToken, Task<TOutput>> operation,
        StepOptions? stepOptions = null,
        CancellationToken cancellationToken = default,
        Func<TOutput, WorkflowStepContext, CancellationToken, Task>? compensation = null)
    {
        ValidateStepKey(stepKey);
        ArgumentNullException.ThrowIfNull(operation);
        var configured = stepOptions ?? new StepOptions();
        configured.Validate();
        var dependencies = ResolveDependencies(configured);
        var compensationMetadata = compensation is null
            ? null
            : new CompensationMetadata(
                stepKey,
                JsonSerializer.Serialize(new RetryPolicy(), serializerOptions),
                null);
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
                    dependencies,
                    compensationMetadata,
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
                            dependencies,
                            compensationMetadata,
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
                    ResolveDependencies(null),
                    null,
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

    /// <summary>
    /// Durably waits for an external signal delivered by
    /// <see cref="WorkflowEngine.SendSignalAsync"/>. Signals are buffered in the
    /// store: one delivered before this wait begins (or arriving while it
    /// waits) is consumed exactly once by the first matching wait. The wait
    /// survives process restarts; when it times out, the step stays recorded as
    /// waiting so a later re-execution can still consume a late signal.
    /// </summary>
    public async Task<T> WaitForSignalAsync<T>(
        string stepKey,
        string signalName,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ValidateStepKey(stepKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(signalName);
        if (timeout is { } waitTimeout && waitTimeout < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        var outputType = SerializationIdentity.TypeId(typeof(T));
        var stepLock = stepLocks.GetOrAdd(stepKey, _ => new SemaphoreSlim(1, 1));
        using var linkedCancellation = CancellationTokenSource
            .CreateLinkedTokenSource(
                workflowCancellationToken,
                cancellationToken);
        await stepLock.WaitAsync(linkedCancellation.Token).ConfigureAwait(false);
        try
        {
            var deadline = timeout is null
                ? (DateTimeOffset?)null
                : timeProvider.GetUtcNow() + timeout.Value;
            while (true)
            {
                var claim = await ClaimAsync(
                    stepKey,
                    signalName,
                    SerializationIdentity.TypeId(typeof(string)),
                    SerializationIdentity.Hash(signalName),
                    outputType,
                    ResolveDependencies(null),
                    null,
                    linkedCancellation.Token).ConfigureAwait(false);
                switch (claim.Disposition)
                {
                    case StepClaimDisposition.Reused:
                        return Deserialize<T>(claim.Step.OutputJson, outputType);
                    case StepClaimDisposition.Busy:
                        await Task.Delay(
                            options.PollInterval,
                            timeProvider,
                            linkedCancellation.Token).ConfigureAwait(false);
                        continue;
                    case StepClaimDisposition.Cancelled:
                        throw new OperationCanceledException(
                            $"Workflow step '{stepKey}' was cancelled.",
                            linkedCancellation.Token);
                    case StepClaimDisposition.Failed:
                        throw new WorkflowStepFailedException(
                            stepKey,
                            claim.Step.Error ?? UnknownFailure(stepKey));
                    case StepClaimDisposition.Waiting:
                    case StepClaimDisposition.Acquired:
                        {
                            var delivery = await store.TryDeliverSignalAsync(
                                claim.Step.Id,
                                ownerId,
                                signalName,
                                timeProvider.GetUtcNow(),
                                linkedCancellation.Token).ConfigureAwait(false);
                            if (delivery is { } delivered)
                            {
                                return Deserialize<T>(delivered.DataJson, outputType);
                            }
                            if (deadline is { } waitDeadline &&
                                timeProvider.GetUtcNow() > waitDeadline)
                            {
                                throw new TimeoutException(
                                    $"Signal '{signalName}' was not delivered before the wait deadline.");
                            }
                            await Task.Delay(
                                options.PollInterval,
                                timeProvider,
                                linkedCancellation.Token).ConfigureAwait(false);
                            continue;
                        }
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
    /// Persists a caller-visible, replay-safe event with optional serialized data.
    /// Emitted events are committed atomically with state transitions and survive
    /// process restarts; subscribers can read them via <c>SubscribeAsync</c> or
    /// <c>GetEventsAsync</c>. Emitting the same key twice does not deduplicate.
    /// If an <see cref="IWorkflowEventPublisher"/> is registered, the committed
    /// event is also forwarded after the store write (best-effort; the store
    /// remains the authoritative source of events).
    /// </summary>
    public async Task EmitAsync<TData>(
        string eventType,
        TData? data = default,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);
        var dataJson = data is null
            ? null
            : JsonSerializer.Serialize(data, serializerOptions);
        var @event = await store.AppendEventAsync(
            WorkflowRunId,
            eventType,
            dataJson,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (eventPublisher is not null)
        {
            try
            {
                await eventPublisher.PublishAsync(
                    @event,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                throw new WorkflowEventPublisherException(
                    "A registered event publisher failed to forward a committed event.",
                    exception);
            }
        }
    }

    /// <summary>
    /// Executes one durable step per input, all in parallel, using step keys
    /// derived from <paramref name="stepKeyPrefix"/> and the item index
    /// (<c>"{prefix}.{index}"</c>). Each item is independently durable: after a
    /// restart, completed items are reused and only unfinished items re-run.
    /// Results are returned in the order of <paramref name="inputs"/>.
    /// </summary>
    public Task<IReadOnlyList<TOutput>> FanOutAsync<TInput, TOutput>(
        string stepKeyPrefix,
        IReadOnlyList<TInput> inputs,
        Func<TInput, WorkflowStepContext, CancellationToken, Task<TOutput>> operation,
        StepOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ValidateStepKey(stepKeyPrefix);
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(operation);
        if (inputs.Count == 0)
            return Task.FromResult<IReadOnlyList<TOutput>>(
                Array.Empty<TOutput>());
        return FanOutCoreAsync(
            stepKeyPrefix,
            inputs,
            operation,
            options,
            cancellationToken);
    }

    private async Task<IReadOnlyList<TOutput>> FanOutCoreAsync<TInput, TOutput>(
        string stepKeyPrefix,
        IReadOnlyList<TInput> inputs,
        Func<TInput, WorkflowStepContext, CancellationToken, Task<TOutput>> operation,
        StepOptions? options,
        CancellationToken cancellationToken)
    {
        var results = new TOutput[inputs.Count];
        var tasks = new Task<TOutput>[inputs.Count];
        for (var i = 0; i < inputs.Count; i++)
        {
            var index = i;
            var input = inputs[index];
            tasks[index] = StepAsync(
                $"{stepKeyPrefix}.{index}",
                input,
                (value, step, ct) => operation(value, step, ct),
                options,
                cancellationToken);
        }
        var completed = await Task.WhenAll(tasks).ConfigureAwait(false);
        completed.CopyTo(results, 0);
        return results;
    }

    /// <summary>
    /// Starts a child workflow and durably waits for its result. The child is a
    /// regular run linked via <see cref="WorkflowRun.ParentRunId"/>; its id is
    /// derived deterministically from the parent and step key so replays reuse
    /// it. Child failure or cancellation propagates to this step.
    /// </summary>
    public async Task<TOutput> StartChildAsync<TInput, TOutput>(
        string stepKey,
        string workflowName,
        string workflowVersion,
        TInput input,
        CancellationToken cancellationToken = default)
    {
        ValidateStepKey(stepKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowName);
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowVersion);
        var request = new ChildStartRequest(
            workflowName,
            workflowVersion,
            JsonSerializer.Serialize(input, serializerOptions),
            SerializationIdentity.TypeId(typeof(TInput)),
            SerializationIdentity.TypeId(typeof(TOutput)));
        var childId = await StepAsync(
            $"{stepKey}:start",
            request,
            (value, _, ct) => CreateChildRunAsync(stepKey, value, ct),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return await WaitForChildAsync<TOutput>(
            $"{stepKey}:wait",
            $"{stepKey}:start",
            childId,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<Guid> CreateChildRunAsync(
        string stepKey,
        ChildStartRequest request,
        CancellationToken cancellationToken)
    {
        var childId = SerializationIdentity.HashId($"{WorkflowRunId:D}:{stepKey}");
        var existing = await store.GetRunAsync(
            childId,
            cancellationToken).ConfigureAwait(false);
        if (existing is not null)
            return childId;
        var now = timeProvider.GetUtcNow();
        await store.CreateRunAsync(
            new WorkflowRun
            {
                Id = childId,
                WorkflowName = request.WorkflowName,
                WorkflowVersion = request.WorkflowVersion,
                Status = WorkflowStatus.Pending,
                CreatedAt = now,
                UpdatedAt = now,
                InputJson = request.InputJson,
                InputType = request.InputType,
                OutputType = request.OutputType,
                ParentRunId = WorkflowRunId
            },
            cancellationToken).ConfigureAwait(false);
        return childId;
    }

    private async Task<TOutput> WaitForChildAsync<TOutput>(
        string stepKey,
        string dependsOnStepKey,
        Guid childId,
        CancellationToken cancellationToken) =>
        await StepAsync(
            stepKey,
            childId,
            (value, _, ct) => AwaitChildCoreAsync<TOutput>(value, ct),
            new StepOptions { DependsOn = [dependsOnStepKey] },
            cancellationToken).ConfigureAwait(false);

    private async Task<TOutput> AwaitChildCoreAsync<TOutput>(
        Guid childId,
        CancellationToken cancellationToken)
    {
        var outputType = SerializationIdentity.TypeId(typeof(TOutput));
        while (true)
        {
            var child = await store.GetRunAsync(childId, cancellationToken)
                .ConfigureAwait(false) ??
                throw new WorkflowStateException(
                    $"Child workflow '{childId:D}' does not exist.");
            switch (child.Status)
            {
                case WorkflowStatus.Completed:
                    if (!string.Equals(
                            child.OutputType,
                            outputType,
                            StringComparison.Ordinal))
                    {
                        throw new WorkflowSerializationException(
                            $"Child workflow result was stored as '{child.OutputType}', not '{outputType}'.");
                    }
                    return Deserialize<TOutput>(child.OutputJson, outputType);
                case WorkflowStatus.Failed:
                    throw new WorkflowExecutionFailedException(
                        childId,
                        child.Error ?? new WorkflowError
                        {
                            Type = typeof(WorkflowStateException).FullName!,
                            Message = $"Child workflow '{childId:D}' failed without persisted details.",
                            Timestamp = timeProvider.GetUtcNow()
                        });
                case WorkflowStatus.Cancelled:
                    throw new OperationCanceledException(
                        $"Child workflow '{childId:D}' was cancelled.",
                        cancellationToken);
            }
            if (executeChildRun is not null)
            {
                await executeChildRun(childId, cancellationToken).ConfigureAwait(false);
            }
            await Task.Delay(
                options.PollInterval,
                timeProvider,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<TOutput> ExecuteClaimedAsync<TInput, TOutput>(
        WorkflowStepRun step,
        TInput input,
        Func<TInput, WorkflowStepContext, CancellationToken, Task<TOutput>> operation,
        StepOptions configured,
        string outputType,
        IReadOnlyCollection<string>? dependencies,
        CompensationMetadata? compensation,
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
                dependencies,
                compensation,
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
        IReadOnlyCollection<string>? dependencies,
        CompensationMetadata? compensation,
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
                LeaseExpiresAt = now + options.LeaseDuration,
                LeaseGeneration = leaseGeneration,
                DependsOn = dependencies,
                Compensation = compensation
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

    private sealed record ChildStartRequest(
        string WorkflowName,
        string WorkflowVersion,
        string InputJson,
        string InputType,
        string OutputType);
}
