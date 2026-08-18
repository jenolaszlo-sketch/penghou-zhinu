using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Penghou.Zhinu;

/// <summary>
/// Runs registered workflows against an explicit durable store. The engine is
/// embedded and does not require a server, scheduler, or message broker.
/// </summary>
public sealed class WorkflowEngine
{
    private readonly IWorkflowStore store;
    private readonly IWorkflowRegistry registry;
    private readonly ZhinuOptions options;
    private readonly JsonSerializerOptions serializerOptions;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<WorkflowEngine> logger;
    private readonly IWorkflowEventPublisher? eventPublisher;
    private readonly string ownerId =
        $"process-{Environment.ProcessId}-{Guid.NewGuid():N}";
    private readonly SemaphoreSlim initializationLock = new(1, 1);
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource>
        runningCancellations = new();
    private volatile bool initialized;

    public WorkflowEngine(
        IWorkflowStore store,
        IWorkflowRegistry registry,
        ZhinuOptions? options = null,
        JsonSerializerOptions? serializerOptions = null,
        TimeProvider? timeProvider = null,
        ILogger<WorkflowEngine>? logger = null,
        IWorkflowEventPublisher? eventPublisher = null)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
        this.options = options ?? new ZhinuOptions();
        this.options.Validate();
        this.serializerOptions = serializerOptions ?? CreateSerializerOptions();
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.logger = logger ?? NullLogger<WorkflowEngine>.Instance;
        this.eventPublisher = eventPublisher;
    }

    /// <summary>Creates a pending durable run without waiting for its execution.</summary>
    public async Task<Guid> StartAsync<TInput>(
        string workflowName,
        string workflowVersion,
        TInput input,
        Guid? workflowRunId = null,
        DateTimeOffset? deadline = null,
        object? metadata = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        var registration = registry.Get(workflowName, workflowVersion);
        if (registration.InputType != typeof(TInput))
        {
            throw new ArgumentException(
                $"Workflow '{workflowName}' version '{workflowVersion}' expects input type '{registration.InputType}', not '{typeof(TInput)}'.",
                nameof(input));
        }
        var id = workflowRunId ?? Guid.NewGuid();
        var inputJson = registration.SerializeInput(input, serializerOptions);
        if (workflowRunId is not null)
        {
            var existing = await store.GetRunAsync(id, cancellationToken)
                .ConfigureAwait(false);
            if (existing is not null)
            {
                if (existing.WorkflowName == workflowName &&
                    existing.WorkflowVersion == workflowVersion &&
                    existing.InputType ==
                        SerializationIdentity.TypeId(registration.InputType) &&
                    existing.InputJson == inputJson)
                {
                    return id;
                }
                throw new WorkflowStateException(
                    $"Workflow run ID '{id:D}' is already associated with a different workflow or input.");
            }
        }
        var now = timeProvider.GetUtcNow();
        await store.CreateRunAsync(
            new WorkflowRun
            {
                Id = id,
                WorkflowName = workflowName,
                WorkflowVersion = workflowVersion,
                Status = WorkflowStatus.Pending,
                CreatedAt = now,
                UpdatedAt = now,
                InputJson = inputJson,
                InputType = SerializationIdentity.TypeId(registration.InputType),
                OutputType = SerializationIdentity.TypeId(registration.OutputType),
                Deadline = deadline,
                MetadataJson = metadata is null
                    ? null
                    : JsonSerializer.Serialize(metadata, serializerOptions)
            },
            cancellationToken).ConfigureAwait(false);
        logger.LogInformation(
            "Created workflow {WorkflowRunId} for {WorkflowName} version {WorkflowVersion}.",
            id,
            workflowName,
            workflowVersion);
        return id;
    }

    /// <summary>Starts, executes, and waits for one workflow result.</summary>
    public async Task<TOutput> RunAsync<TInput, TOutput>(
        string workflowName,
        string workflowVersion,
        TInput input,
        Guid? workflowRunId = null,
        DateTimeOffset? deadline = null,
        object? metadata = null,
        CancellationToken cancellationToken = default)
    {
        var id = await StartAsync(
            workflowName,
            workflowVersion,
            input,
            workflowRunId,
            deadline,
            metadata,
            cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(id, cancellationToken).ConfigureAwait(false);
        return await WaitForCompletionAsync<TOutput>(
            id,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Executes or resumes one run. Completed step results reconstruct values
    /// while code outside step boundaries may execute again.
    /// </summary>
    public async Task ExecuteAsync(
        Guid workflowRunId,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteCoreAsync(
            workflowRunId,
            cancellationToken,
            0).ConfigureAwait(false);
    }

    private async Task ExecuteCoreAsync(
        Guid workflowRunId,
        CancellationToken cancellationToken,
        int depth)
    {
        if (depth > options.MaxNestingDepth)
            return;
        var run = await store.GetRunAsync(workflowRunId, cancellationToken)
            .ConfigureAwait(false) ??
            throw new KeyNotFoundException(
                $"Workflow '{workflowRunId:D}' does not exist.");
        if (IsTerminal(run.Status))
            return;
        var now = timeProvider.GetUtcNow();
        if (!await store.TryClaimRunAsync(
                workflowRunId,
                ownerId,
                now,
                now + options.LeaseDuration,
                cancellationToken).ConfigureAwait(false))
        {
            return;
        }
        if (run.Deadline is { } deadline && now > deadline)
        {
            await store.FailRunAsync(
                workflowRunId,
                ownerId,
                WorkflowError.FromException(
                    new TimeoutException(
                        $"Workflow '{workflowRunId:D}' exceeded its deadline of {deadline:O}."),
                    now),
                now,
                CancellationToken.None).ConfigureAwait(false);
            return;
        }

        using var runCancellation = CancellationTokenSource
            .CreateLinkedTokenSource(cancellationToken);
        if (!runningCancellations.TryAdd(workflowRunId, runCancellation))
        {
            await store.ReleaseRunLeaseAsync(
                workflowRunId,
                ownerId,
                timeProvider.GetUtcNow(),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        await using var renewal = new LeaseRenewal(
            timeProvider,
            options.LeaseRenewalInterval,
            token => store.RenewRunLeaseAsync(
                workflowRunId,
                ownerId,
                timeProvider.GetUtcNow() + options.LeaseDuration,
                token));
        try
        {
            if (!registry.TryGet(
                    run.WorkflowName,
                    run.WorkflowVersion,
                    out var registration))
            {
                throw new WorkflowDefinitionUnavailableException(
                    run.WorkflowName,
                    run.WorkflowVersion);
            }
            ValidateRunTypes(run, registration!);
            var context = new WorkflowContext(
                workflowRunId,
                store,
                ownerId,
                options,
                serializerOptions,
                timeProvider,
                runCancellation.Token,
                eventPublisher,
                executeChildRun: (childId, childCancellation) =>
                    ExecuteCoreAsync(
                        childId,
                        childCancellation,
                        depth + 1));
            logger.LogInformation(
                "Executing workflow {WorkflowRunId} ({WorkflowName} {WorkflowVersion}).",
                workflowRunId,
                run.WorkflowName,
                run.WorkflowVersion);
            var outputJson = await registration!.ExecuteAsync(
                context,
                run.InputJson ?? "null",
                serializerOptions,
                runCancellation.Token).ConfigureAwait(false);
            await store.CompleteRunAsync(
                workflowRunId,
                ownerId,
                outputJson,
                SerializationIdentity.TypeId(registration.OutputType),
                timeProvider.GetUtcNow(),
                CancellationToken.None).ConfigureAwait(false);
            logger.LogInformation(
                "Completed workflow {WorkflowRunId}.",
                workflowRunId);
        }
        catch (OperationCanceledException) when (runCancellation.IsCancellationRequested)
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
        catch (Exception exception)
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
            }
            logger.LogError(
                exception,
                "Workflow {WorkflowRunId} failed.",
                workflowRunId);
        }
        finally
        {
            runningCancellations.TryRemove(workflowRunId, out _);
        }
    }

    /// <summary>Recovers expired leases and executes every currently runnable run.</summary>
    public async Task<int> RunAvailableAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await store.RecoverExpiredLeasesAsync(
            timeProvider.GetUtcNow(),
            cancellationToken).ConfigureAwait(false);
        var ids = await store.GetRunnableRunIdsAsync(
            timeProvider.GetUtcNow(),
            options.ScanBatchSize,
            cancellationToken).ConfigureAwait(false);
        using var concurrency = new SemaphoreSlim(
            options.MaxConcurrentWorkflows,
            options.MaxConcurrentWorkflows);
        await Task.WhenAll(ids.Select(async id =>
        {
            await concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await ExecuteAsync(id, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                concurrency.Release();
            }
        })).ConfigureAwait(false);
        return ids.Count;
    }

    /// <summary>Persists cancellation and signals an active local execution.</summary>
    public async Task CancelAsync(
        Guid workflowRunId,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await store.CancelRunAsync(
            workflowRunId,
            timeProvider.GetUtcNow(),
            cancellationToken).ConfigureAwait(false);
        if (runningCancellations.TryGetValue(
                workflowRunId,
                out var runningCancellation))
        {
            await runningCancellation.CancelAsync().ConfigureAwait(false);
        }
    }

    public async Task<WorkflowRun?> GetRunAsync(
        Guid workflowRunId,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        return await store.GetRunAsync(workflowRunId, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Queries persisted runs with optional filters and cursor pagination.</summary>
    public async Task<IReadOnlyList<WorkflowRun>> GetRunsAsync(
        RunQuery query,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        return await store.GetRunsAsync(query, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Replaces a run's metadata without affecting its identity or contracts.</summary>
    public async Task<WorkflowRun?> UpdateRunMetadataAsync(
        Guid workflowRunId,
        object? metadata,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        var metadataJson = metadata is null
            ? null
            : JsonSerializer.Serialize(metadata, serializerOptions);
        return await store.UpdateRunMetadataAsync(
            workflowRunId,
            metadataJson,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes runs older than <paramref name="olderThan"/> (optionally limited
    /// to specific statuses) and returns the number deleted. Steps and events of
    /// purged runs are removed via cascade. Prefer purging terminal runs;
    /// deleting an active run abandons its execution record.
    /// </summary>
    public async Task<int> PurgeRunsAsync(
        DateTimeOffset olderThan,
        IReadOnlyList<WorkflowStatus>? statuses = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        return await store.PurgeRunsAsync(
            olderThan,
            statuses,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Invalidates the step <paramref name="stepKey"/> and every step created
    /// at or after it, then resets the run to <see cref="WorkflowStatus.Pending"/>
    /// so the next execution re-runs the target step and its sub-tree while
    /// reusing committed results from the prefix. If this process is currently
    /// executing the run, its execution is cancelled first (best-effort).
    /// Returns the number of steps invalidated, including the target.
    /// </summary>
    public async Task<int> RestartStepAsync(
        Guid workflowRunId,
        string stepKey,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        if (runningCancellations.TryGetValue(
                workflowRunId,
                out var runningCancellation))
        {
            await runningCancellation.CancelAsync().ConfigureAwait(false);
        }
        return await store.RestartStepAsync(
            workflowRunId,
            stepKey,
            timeProvider.GetUtcNow(),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Buffers an external signal for <paramref name="workflowRunId"/> under
    /// <paramref name="signalName"/>. Signals are consumed by a
    /// <c>WaitForSignalAsync</c> wait in the workflow; signals sent before any
    /// wait are held until a matching wait appears. A signal is delivered to
    /// exactly one waiting step.
    /// </summary>
    public async Task SendSignalAsync(
        Guid workflowRunId,
        string signalName,
        object? data = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        ArgumentException.ThrowIfNullOrWhiteSpace(signalName);
        var dataJson = data is null
            ? null
            : JsonSerializer.Serialize(data, serializerOptions);
        await store.SendSignalAsync(
            workflowRunId,
            signalName,
            dataJson,
            cancellationToken).ConfigureAwait(false);
        logger.LogInformation(
            "Buffered signal '{SignalName}' for workflow {WorkflowRunId}.",
            signalName,
            workflowRunId);
    }

    public async Task<IReadOnlyList<WorkflowStepRun>> GetStepsAsync(
        Guid workflowRunId,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        return await store.GetStepsAsync(workflowRunId, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<WorkflowEvent>> GetEventsAsync(
        Guid workflowRunId,
        long afterSequence = 0,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        if (afterSequence < 0)
            throw new ArgumentOutOfRangeException(nameof(afterSequence));
        if (limit is < 1 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(limit));
        return await store.GetEventsAsync(
            workflowRunId,
            afterSequence,
            limit,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns a point-in-time progress snapshot of a run and its child-run
    /// subtree: the run itself, its durable steps, its recent events, and
    /// (recursively) the same shape for each child run. Returns null when the
    /// run does not exist.
    /// </summary>
    public async Task<WorkflowRunProgress?> GetRunProgressAsync(
        Guid workflowRunId,
        RunProgressOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        var progressOptions = options ?? new RunProgressOptions();
        progressOptions.Validate();
        var runs = await store.GetRunSubtreeAsync(
            workflowRunId,
            progressOptions.MaxDepth,
            cancellationToken).ConfigureAwait(false);
        if (runs.Count == 0)
            return null;
        var byId = runs.ToDictionary(run => run.Id);
        var childrenByParent = new Dictionary<Guid, List<WorkflowRunProgress>>();
        for (var index = runs.Count - 1; index >= 0; index--)
        {
            var run = runs[index];
            var node = await CreateProgressSnapshotAsync(
                run,
                progressOptions,
                cancellationToken).ConfigureAwait(false);
            var children = childrenByParent.GetValueOrDefault(run.Id);
            if (children is not null)
                node = node with { Children = children.ToArray() };
            if (run.Id == workflowRunId)
                return node;
            if (run.ParentRunId is { } parentId && byId.ContainsKey(parentId))
            {
                if (!childrenByParent.TryGetValue(parentId, out var parentChildren))
                {
                    parentChildren = childrenByParent[parentId] = [];
                }
                parentChildren.Insert(0, node);
            }
        }
        return null;
    }

    private async Task<WorkflowRunProgress> CreateProgressSnapshotAsync(
        WorkflowRun run,
        RunProgressOptions options,
        CancellationToken cancellationToken)
    {
        var steps = await store.GetStepsAsync(run.Id, cancellationToken).ConfigureAwait(false);
        var events = options.IncludeEvents
            ? await store.GetEventsAsync(
                run.Id,
                afterSequence: 0,
                limit: options.EventsLimit,
                cancellationToken).ConfigureAwait(false)
            : [];
        return new WorkflowRunProgress
        {
            Run = run,
            Steps = steps,
            Events = events
        };
    }

    /// <summary>Polls durable state without blocking a thread until a run terminates.</summary>
    public async Task<TOutput> WaitForCompletionAsync<TOutput>(
        Guid workflowRunId,
        DateTimeOffset? deadline = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        while (true)
        {
            if (deadline is { } waitDeadline &&
                timeProvider.GetUtcNow() > waitDeadline)
            {
                throw new TimeoutException(
                    $"Workflow '{workflowRunId:D}' did not complete before the wait deadline of {waitDeadline:O}.");
            }
            var run = await store.GetRunAsync(workflowRunId, cancellationToken)
                .ConfigureAwait(false) ??
                throw new KeyNotFoundException(
                    $"Workflow '{workflowRunId:D}' does not exist.");
            switch (run.Status)
            {
                case WorkflowStatus.Completed:
                    var expectedType = SerializationIdentity.TypeId(typeof(TOutput));
                    if (!string.Equals(
                            run.OutputType,
                            expectedType,
                            StringComparison.Ordinal))
                    {
                        throw new WorkflowSerializationException(
                            $"Workflow result was stored as '{run.OutputType}', not '{expectedType}'.");
                    }
                    try
                    {
                        var output = JsonSerializer.Deserialize<TOutput>(
                            run.OutputJson ?? "null",
                            serializerOptions);
                        if (output is null && default(TOutput) is not null)
                        {
                            throw new WorkflowSerializationException(
                                $"Stored workflow result '{expectedType}' was null.");
                        }
                        return output!;
                    }
                    catch (JsonException exception)
                    {
                        throw new WorkflowSerializationException(
                            $"Workflow result could not be deserialized as '{expectedType}'.",
                            exception);
                    }
                case WorkflowStatus.Failed:
                    throw new WorkflowExecutionFailedException(
                        workflowRunId,
                        run.Error ?? new WorkflowError
                        {
                            Type = typeof(WorkflowStateException).FullName!,
                            Message = "Workflow failed without persisted details.",
                            Timestamp = run.UpdatedAt
                        });
                case WorkflowStatus.Cancelled:
                    throw new OperationCanceledException(
                        $"Workflow '{workflowRunId:D}' was cancelled.");
            }
            await Task.Delay(
                options.PollInterval,
                timeProvider,
                cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Streams committed events and survives missed in-process notifications.</summary>
    public async IAsyncEnumerable<WorkflowEvent> SubscribeAsync(
        Guid workflowRunId,
        long afterSequence = 0,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var cursor = afterSequence;
        while (!cancellationToken.IsCancellationRequested)
        {
            var events = await GetEventsAsync(
                workflowRunId,
                cursor,
                100,
                cancellationToken).ConfigureAwait(false);
            foreach (var item in events)
            {
                cursor = item.Sequence;
                yield return item;
            }
            var run = await GetRunAsync(workflowRunId, cancellationToken)
                .ConfigureAwait(false);
            if (run is null || IsTerminal(run.Status) && events.Count == 0)
                yield break;
            if (events.Count == 0)
            {
                await Task.Delay(
                    options.PollInterval,
                    timeProvider,
                    cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async ValueTask EnsureInitializedAsync(
        CancellationToken cancellationToken)
    {
        if (initialized)
            return;
        await initializationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (initialized)
                return;
            await store.InitializeAsync(cancellationToken).ConfigureAwait(false);
            await store.RecoverExpiredLeasesAsync(
                timeProvider.GetUtcNow(),
                cancellationToken).ConfigureAwait(false);
            initialized = true;
        }
        finally
        {
            initializationLock.Release();
        }
    }

    private static void ValidateRunTypes(
        WorkflowRun run,
        IWorkflowRegistration registration)
    {
        var inputType = SerializationIdentity.TypeId(registration.InputType);
        var outputType = SerializationIdentity.TypeId(registration.OutputType);
        if (!string.Equals(run.InputType, inputType, StringComparison.Ordinal) ||
            !string.Equals(run.OutputType, outputType, StringComparison.Ordinal))
        {
            throw new WorkflowSerializationException(
                $"Stored workflow type contract does not match registered workflow '{run.WorkflowName}' version '{run.WorkflowVersion}'.");
        }
    }

    private static bool IsTerminal(WorkflowStatus status) =>
        status is WorkflowStatus.Completed or WorkflowStatus.Failed or
            WorkflowStatus.Cancelled;

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
