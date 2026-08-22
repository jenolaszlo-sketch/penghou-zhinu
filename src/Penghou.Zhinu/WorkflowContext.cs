using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Penghou.Zhinu.Context;

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
    private readonly Action<Guid>? onEventAppended;
    private readonly StepLockManager stepLocks = new();
    private readonly DependencyTracker dependencies = new();
    private readonly ChildRunCoordinator childRuns;
    private readonly IReadOnlyDictionary<string, WorkflowStepRun>? replaySteps;
    private readonly IReadOnlyDictionary<string, WorkflowStepCompensation>?
        rollbackCompensations;
    private readonly List<CompensationInvocation> rollbackInvocations = [];

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
        Func<Guid, CancellationToken, Task>? executeChildRun = null,
        IReadOnlyDictionary<string, WorkflowStepRun>? replaySteps = null,
        IReadOnlyDictionary<string, WorkflowStepCompensation>? rollbackCompensations = null,
        Action<Guid>? onEventAppended = null)
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
        this.replaySteps = replaySteps;
        this.rollbackCompensations = rollbackCompensations;
        this.onEventAppended = onEventAppended;
        childRuns = new ChildRunCoordinator(
            store,
            this.options,
            this.serializerOptions,
            this.timeProvider,
            executeChildRun);
    }

    public Guid WorkflowRunId { get; }

    /// <summary>
    /// Publishes a run-level artifact that is not owned by a particular step.
    /// Prefer <see cref="WorkflowStepContext.PublishArtifactAsync"/> for
    /// artifacts created inside durable steps so exact provenance is retained.
    /// </summary>
    public Task<WorkflowArtifactReference> PublishArtifactAsync(
        WorkflowArtifactDescriptor artifact,
        CancellationToken cancellationToken = default)
    {
        ValidateArtifact(artifact);
        return PublishArtifactCoreAsync(artifact, null, cancellationToken);
    }

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
        return dependencies.Declare(stepKeys);
    }

    private IReadOnlyCollection<string>? ResolveDependencies(StepOptions? options) =>
        dependencies.Resolve(options?.DependsOn);

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
        if (dependencies is not null && dependencies.Contains(stepKey))
            throw new WorkflowStateException($"Step '{stepKey}' cannot depend on itself.");
        var compensationMetadata = compensation is null
            ? null
            : new CompensationMetadata(
                stepKey,
                JsonSerializer.Serialize(configured.Retry, serializerOptions),
                configured.ExecutionTimeout);
        var inputJson = JsonSerializer.Serialize(input, serializerOptions);
        var inputType = SerializationIdentity.TypeId(typeof(TInput));
        var outputType = SerializationIdentity.TypeId(typeof(TOutput));
        using var linkedCancellation = CancellationTokenSource
            .CreateLinkedTokenSource(
                workflowCancellationToken,
                cancellationToken);
        var stepLock = await stepLocks.AcquireAsync(stepKey, linkedCancellation.Token).ConfigureAwait(false);
        try
        {
            if (IsRollback)
            {
                return ResolveRollbackStep<TOutput>(
                    stepKey,
                    outputType,
                    compensation);
            }
            if (dependencies is not null)
            {
                var existing = await store.GetStepDependenciesAsync(WorkflowRunId, linkedCancellation.Token)
                    .ConfigureAwait(false);
                var existingForStep = existing.Where(e => e.StepKey == stepKey).Select(e => e.DependsOnStepKey).ToHashSet(StringComparer.Ordinal);
                var newEdges = dependencies.Where(d => !existingForStep.Contains(d)).ToList();
                if (newEdges.Count > 0)
                {
                    var combined = new List<StepDependency>(existing);
                    foreach (var dep in newEdges) combined.Add(new StepDependency(stepKey, dep));
                    if (WorkflowDependencyValidator.HasCycle(combined))
                        throw new WorkflowStateException($"Adding dependencies for step '{stepKey}' would create a cycle.");
                    var steps = await store.GetStepsAsync(WorkflowRunId, linkedCancellation.Token).ConfigureAwait(false);
                    if (steps.Any(s => s.StepKey == stepKey && s.Status == StepStatus.Completed))
                        throw new WorkflowStateException($"Cannot add dependencies for step '{stepKey}' after it has completed.");
                }
            }
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
                        ZhinuDiagnostics.StepsReusedCounter.Add(1);
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
            stepLock.Dispose();
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
        using var activity = ZhinuDiagnostics.StartActivity(ZhinuDiagnostics.Activities.DelayWait);
        activity?.SetTag(ZhinuDiagnostics.Attributes.WorkflowRunId, WorkflowRunId);
        activity?.SetTag(ZhinuDiagnostics.Attributes.StepKey, stepKey);
        if (delay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(delay));
        var inputJson = JsonSerializer.Serialize(delay.Ticks, serializerOptions);
        using var linkedCancellation = CancellationTokenSource
            .CreateLinkedTokenSource(
                workflowCancellationToken,
                cancellationToken);
        var stepLock = await stepLocks.AcquireAsync(stepKey, linkedCancellation.Token).ConfigureAwait(false);
        try
        {
            if (IsRollback)
            {
                if (replaySteps!.TryGetValue(stepKey, out var stored))
                    return;
                throw new WorkflowStateException(
                    $"Step '{stepKey}' has no committed delay to replay during rollback.");
            }
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
            stepLock.Dispose();
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
    public Task<T> WaitForSignalAsync<T>(
        string stepKey,
        SignalDefinition<T> signal,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(signal);
        return WaitForSignalAsync<T>(stepKey, signal.Name, timeout, cancellationToken);
    }

    public async Task<T> WaitForSignalAsync<T>(
        string stepKey,
        string signalName,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ValidateStepKey(stepKey);
        using var activity = ZhinuDiagnostics.StartActivity(ZhinuDiagnostics.Activities.SignalWait);
        activity?.SetTag(ZhinuDiagnostics.Attributes.WorkflowRunId, WorkflowRunId);
        activity?.SetTag(ZhinuDiagnostics.Attributes.StepKey, stepKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(signalName);
        if (timeout is { } waitTimeout && waitTimeout < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        var outputType = SerializationIdentity.TypeId(typeof(T));
        using var linkedCancellation = CancellationTokenSource
            .CreateLinkedTokenSource(
                workflowCancellationToken,
                cancellationToken);
        var stepLock = await stepLocks.AcquireAsync(stepKey, linkedCancellation.Token).ConfigureAwait(false);
        try
        {
            if (IsRollback)
            {
                if (replaySteps!.TryGetValue(stepKey, out var stored))
                    return Deserialize<T>(stored.OutputJson, outputType);
                throw new WorkflowStateException(
                    $"Step '{stepKey}' has no committed signal result to replay during rollback.");
            }
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
                                ZhinuDiagnostics.SignalsDeliveredCounter.Add(1);
                                activity?.SetStatus(ActivityStatusCode.Ok);
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
            stepLock.Dispose();
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
        if (IsRollback)
            return;
        var dataJson = data is null
            ? null
            : JsonSerializer.Serialize(data, serializerOptions);
        var @event = await store.AppendEventAsync(
            WorkflowRunId,
            eventType,
            dataJson,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        onEventAppended?.Invoke(WorkflowRunId);
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
    /// derived deterministically from the parent run, step key, and the
    /// <c>child:start</c> step revision, so replays reuse the child while
    /// restarting the step creates a fresh child. Child failure or cancellation
    /// propagates to this step.
    /// </summary>
    public Task<TOutput> StartChildAsync<TInput, TOutput>(
        string stepKey,
        string workflowName,
        string workflowVersion,
        TInput input,
        CancellationToken cancellationToken = default) =>
        StartChildCoreAsync<TInput, TOutput>(
            stepKey,
            workflowName,
            workflowVersion,
            input,
            options: null,
            cancellationToken);

    /// <summary>
    /// Starts a child workflow with explicit deadline and metadata semantics.
    /// The effective child deadline is the earlier of the parent's deadline and
    /// <see cref="ChildRunOptions.Deadline"/>; metadata is only inherited when
    /// <see cref="ChildRunOptions.InheritMetadata"/> is set.
    /// </summary>
    public Task<TOutput> StartChildAsync<TInput, TOutput>(
        string stepKey,
        string workflowName,
        string workflowVersion,
        TInput input,
        ChildRunOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return StartChildCoreAsync<TInput, TOutput>(
            stepKey,
            workflowName,
            workflowVersion,
            input,
            options,
            cancellationToken);
    }

    private async Task<TOutput> StartChildCoreAsync<TInput, TOutput>(
        string stepKey,
        string workflowName,
        string workflowVersion,
        TInput input,
        ChildRunOptions? options,
        CancellationToken cancellationToken)
    {
        ValidateStepKey(stepKey);
        using var activity = ZhinuDiagnostics.StartActivity(ZhinuDiagnostics.Activities.ChildExecute);
        activity?.SetTag(ZhinuDiagnostics.Attributes.WorkflowRunId, WorkflowRunId);
        activity?.SetTag(ZhinuDiagnostics.Attributes.StepKey, stepKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowName);
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowVersion);
        var request = new ChildRunCoordinator.ChildStartRequest(
            workflowName,
            workflowVersion,
            JsonSerializer.Serialize(input, serializerOptions),
            SerializationIdentity.TypeId(typeof(TInput)),
            SerializationIdentity.TypeId(typeof(TOutput)),
            options?.Deadline,
            options?.Metadata is null
                ? null
                : JsonSerializer.Serialize(options.Metadata, serializerOptions),
            options?.InheritMetadata ?? false);
        var childId = await StepAsync(
            $"{stepKey}:start",
            request,
            (value, step, ct) => childRuns.CreateChildRunAsync(
                WorkflowRunId,
                stepKey,
                step.Revision,
                value,
                ct),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return await StepAsync(
            $"{stepKey}:wait",
            childId,
            (value, _, ct) => childRuns.AwaitChildCoreAsync<TOutput>(
                value,
                ct),
            new StepOptions { DependsOn = [$"{stepKey}:start"] },
            cancellationToken).ConfigureAwait(false);
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
        using var activity = ZhinuDiagnostics.StartActivity(
            ZhinuDiagnostics.Activities.StepExecute);
        activity?.SetTag(ZhinuDiagnostics.Attributes.WorkflowRunId, WorkflowRunId);
        activity?.SetTag(ZhinuDiagnostics.Attributes.StepId, step.Id);
        activity?.SetTag(ZhinuDiagnostics.Attributes.StepKey, step.StepKey);
        activity?.SetTag(ZhinuDiagnostics.Attributes.StepRevision, step.Revision);
        activity?.SetTag(ZhinuDiagnostics.Attributes.LeaseGeneration, step.LeaseGeneration);
        var started = timeProvider.GetTimestamp();
        ZhinuDiagnostics.StepsExecutedCounter.Add(1);
        try
        {
            while (true)
            {
                activity?.SetTag(ZhinuDiagnostics.Attributes.StepAttempt, step.Attempt);
                using var timeoutCancellation = configured.ExecutionTimeout is null
                    ? null
                    : new CancellationTokenSource(configured.ExecutionTimeout.Value, timeProvider);
                using var executionCancellation = timeoutCancellation is null
                    ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                    : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCancellation.Token);
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
                            step.Attempt,
                            step.Revision,
                            false,
                            (artifact, token) => PublishStepArtifactAsync(
                                step, artifact, token)),
                        executionCancellation.Token).ConfigureAwait(false);
                    var outputJson = JsonSerializer.Serialize(output, serializerOptions);
                    await store.CompleteStepAsync(
                        step.Id, ownerId, outputJson, timeProvider.GetUtcNow(), cancellationToken)
                        .ConfigureAwait(false);
                    activity?.SetStatus(ActivityStatusCode.Ok);
                    return output;
                }
                catch (OperationCanceledException) when (
                    timeoutCancellation?.IsCancellationRequested == true &&
                    !cancellationToken.IsCancellationRequested)
                {
                    var timeout = new TimeoutException(
                        $"Workflow step '{step.StepKey}' exceeded its execution timeout.");
                    step = await RecordFailureAsync(
                        step, timeout, configured.Retry, outputType, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    step = await RecordFailureAsync(
                        step, exception, configured.Retry, outputType, cancellationToken)
                        .ConfigureAwait(false);
                }

                if (step.Status == StepStatus.Failed)
                {
                    ZhinuDiagnostics.StepsFailedCounter.Add(1);
                    throw new WorkflowStepFailedException(
                        step.StepKey, step.Error ?? UnknownFailure(step.StepKey));
                }
                await WaitUntilAsync(step.AvailableAt, cancellationToken).ConfigureAwait(false);
                var claim = await ClaimAsync(
                    step.StepKey, step.InputJson, step.InputType, step.InputHash,
                    outputType, dependencies, compensation, cancellationToken)
                    .ConfigureAwait(false);
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
        catch (Exception exception)
        {
            ZhinuDiagnostics.RecordException(activity, exception);
            throw;
        }
        finally
        {
            ZhinuDiagnostics.StepDurationHistogram.Record(
                timeProvider.GetElapsedTime(started).TotalSeconds);
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
        if (retryAt is not null)
        {
            ZhinuDiagnostics.StepsRetriedCounter.Add(1);
            Activity.Current?.SetTag(ZhinuDiagnostics.Attributes.RetryScheduled, true);
        }
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

    private bool IsRollback => replaySteps is not null;

    /// <summary>
    /// A compensation registered during a rollback replay, bound to the
    /// committed forward result the workflow produced and ready to execute.
    /// </summary>
    internal sealed record CompensationInvocation(
        string StepKey,
        WorkflowStepCompensation Compensation,
        Func<CancellationToken, Task> Execute);

    /// <summary>Compensations registered while the workflow replayed in rollback mode.</summary>
    internal IReadOnlyList<CompensationInvocation> RollbackInvocations =>
        rollbackInvocations;

    private TOutput ResolveRollbackStep<TOutput>(
        string stepKey,
        string outputType,
        Func<TOutput, WorkflowStepContext, CancellationToken, Task>? compensation)
    {
        if (rollbackCompensations is not null &&
            rollbackCompensations.TryGetValue(stepKey, out var row) &&
            row.InputJson is not null)
        {
            if (compensation is null)
            {
                throw new WorkflowStateException(
                    $"Workflow definition no longer registers a compensation for step '{stepKey}'.");
            }
            var result = Deserialize<TOutput>(row.InputJson, outputType);
            var stepContext = new WorkflowStepContext(
                WorkflowRunId,
                row.Id,
                stepKey,
                row.Attempt,
                row.Revision,
                isCompensation: true);
            rollbackInvocations.Add(new CompensationInvocation(
                stepKey,
                row,
                ct => compensation(result, stepContext, ct)));
            return result;
        }
        if (replaySteps!.TryGetValue(stepKey, out var stored))
        {
            return Deserialize<TOutput>(stored.OutputJson, outputType);
        }
        throw new WorkflowStateException(
            $"Step '{stepKey}' has no committed result to replay during rollback.");
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

    private T Deserialize<T>(string? json, string expectedType) =>
        StepResultSerializer.Deserialize<T>(json, expectedType, serializerOptions);

    private static WorkflowError UnknownFailure(string stepKey) => new()
    {
        Type = typeof(WorkflowStateException).FullName!,
        Message = $"Step '{stepKey}' has no persisted failure details.",
        Timestamp = DateTimeOffset.MinValue
    };

    private static void ValidateStepKey(string stepKey) =>
        ArgumentException.ThrowIfNullOrWhiteSpace(stepKey);

    private ValueTask<WorkflowArtifactReference> PublishStepArtifactAsync(
        WorkflowStepRun step,
        WorkflowArtifactDescriptor artifact,
        CancellationToken cancellationToken)
    {
        ValidateArtifact(artifact);
        return new ValueTask<WorkflowArtifactReference>(
            PublishArtifactCoreAsync(artifact, step, cancellationToken));
    }

    private async Task<WorkflowArtifactReference> PublishArtifactCoreAsync(
        WorkflowArtifactDescriptor artifact,
        WorkflowStepRun? step,
        CancellationToken cancellationToken)
    {
        using var activity = ZhinuDiagnostics.StartActivity(
            ZhinuDiagnostics.Activities.ArtifactPublish);
        activity?.SetTag(ZhinuDiagnostics.Attributes.WorkflowRunId, WorkflowRunId);
        activity?.SetTag(ZhinuDiagnostics.Attributes.StepKey, step?.StepKey);
        activity?.SetTag(ZhinuDiagnostics.Attributes.StepRevision, step?.Revision);
        activity?.SetTag(ZhinuDiagnostics.Attributes.ArtifactName, artifact.Name);
        activity?.SetTag(ZhinuDiagnostics.Attributes.ArtifactType, artifact.ArtifactType);
        try
        {
            var validationContext = new ArtifactValidationContext
            {
                WorkflowRunId = WorkflowRunId,
                ProducerStepKey = step?.StepKey,
                ProducerStepRevision = step?.Revision
            };
            foreach (var validator in options.ArtifactValidators)
            {
                await validator.ValidateAsync(
                    artifact,
                    validationContext,
                    cancellationToken).ConfigureAwait(false);
            }
            var publication = await store.PublishArtifactAsync(
                new ArtifactPublicationRequest
                {
                    WorkflowRunId = WorkflowRunId,
                    StepExecutionId = step?.Id,
                    ProducerStepKey = step?.StepKey,
                    ProducerStepRevision = step?.Revision,
                    Artifact = artifact,
                    Now = timeProvider.GetUtcNow()
                },
                cancellationToken).ConfigureAwait(false);
            activity?.SetTag(
                ZhinuDiagnostics.Attributes.ArtifactId,
                publication.Artifact.Id);
            activity?.SetTag(
                ZhinuDiagnostics.Attributes.ArtifactRevision,
                publication.Artifact.Revision);
            activity?.SetTag(
                ZhinuDiagnostics.Attributes.ArtifactCreated,
                publication.Created);
            if (publication.Created)
            {
                ZhinuDiagnostics.ArtifactsPublishedCounter.Add(1);
                onEventAppended?.Invoke(WorkflowRunId);
                if (eventPublisher is not null && publication.Event is not null)
                {
                    try
                    {
                        await eventPublisher.PublishAsync(
                            publication.Event,
                            cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        throw new WorkflowEventPublisherException(
                            "A registered event publisher failed to forward a committed artifact event.",
                            exception);
                    }
                }
            }
            activity?.SetStatus(ActivityStatusCode.Ok);
            return publication.Artifact;
        }
        catch (Exception exception)
        {
            ZhinuDiagnostics.RecordException(activity, exception);
            throw;
        }
    }

    private static void ValidateArtifact(WorkflowArtifactDescriptor artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifact.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifact.ArtifactType);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifact.Location);
    }

    private readonly record struct Unit
    {
        public static Unit Value => default;
    }

    private readonly record struct DurableDelayMarker;
}
