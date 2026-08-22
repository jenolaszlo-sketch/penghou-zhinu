using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Penghou.Zhinu.Execution.Outcomes;
using Penghou.Zhinu.Execution.Steps;

namespace Penghou.Zhinu;

/// <summary>
/// Runs registered workflows against an explicit durable store. The engine is
/// embedded and does not require a server, scheduler, or message broker.
/// </summary>
public sealed class WorkflowEngine : IAsyncDisposable
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
    private readonly LeaseRecoveryScheduler leaseRecovery;
    private readonly RunExecutionPipeline executionPipeline;
    private readonly RunScanner scanner;
    private readonly RollbackCoordinator rollbackCoordinator;
    private readonly RollbackAndRestartCoordinator rollbackAndRestart;
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource>
        runningCancellations = new();
    private readonly ConcurrentDictionary<Guid, Channel<byte>> eventChannels = new();
    private int disposed;

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
        leaseRecovery = new LeaseRecoveryScheduler(
            store,
            this.options,
            this.timeProvider);
        var outcomeHandler = new RunOutcomeHandler(
            store,
            ownerId,
            this.timeProvider,
            this.logger,
            NotifyEventAppended);
        var compensationExecutor = new CompensationExecutor(
            store,
            registry,
            this.options,
            this.serializerOptions,
            this.timeProvider,
            eventPublisher,
            ownerId,
            NotifyEventAppended);
        rollbackCoordinator = new RollbackCoordinator(
            store,
            this.options,
            this.serializerOptions,
            this.timeProvider,
            this.logger,
            ownerId,
            NotifyEventAppended,
            compensationExecutor);
        rollbackAndRestart = new RollbackAndRestartCoordinator(
            store,
            this.options,
            this.serializerOptions,
            this.timeProvider,
            this.logger,
            ownerId,
            NotifyEventAppended,
            compensationExecutor);
        executionPipeline = new RunExecutionPipeline(
            store,
            registry,
            this.options,
            this.serializerOptions,
            this.timeProvider,
            this.logger,
            eventPublisher,
            ownerId,
            runningCancellations,
            NotifyEventAppended,
            (workflowRunId, cancellationToken) =>
                rollbackAndRestart.ResumeAsync(
                    workflowRunId,
                    cancellationToken),
            outcomeHandler);
        scanner = new RunScanner(
            store,
            this.options,
            this.timeProvider,
            leaseRecovery,
            (workflowRunId, cancellationToken) =>
                ExecuteAsync(workflowRunId, cancellationToken));
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
        ThrowIfDisposed();
        await leaseRecovery.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
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
                    : JsonSerializer.Serialize(metadata, serializerOptions),
                TraceId = (Activity.Current?.TraceId ?? ActivityTraceId.CreateRandom())
                    .ToHexString()
            },
            cancellationToken).ConfigureAwait(false);
        logger.LogInformation(
            "Created workflow {WorkflowRunId} for {WorkflowName} version {WorkflowVersion}.",
            id,
            workflowName,
            workflowVersion);
        ZhinuDiagnostics.RunsStartedCounter.Add(
            1,
            new KeyValuePair<string, object?>(ZhinuDiagnostics.Attributes.WorkflowName, workflowName),
            new KeyValuePair<string, object?>(ZhinuDiagnostics.Attributes.WorkflowVersion, workflowVersion));
        return id;
    }

    /// <summary>Starts a run and returns a typed durable handle.</summary>
    public async Task<WorkflowHandle<TOutput>> StartHandleAsync<TInput, TOutput>(
        string workflowName, string workflowVersion, TInput input,
        Guid? workflowRunId = null, DateTimeOffset? deadline = null,
        object? metadata = null, CancellationToken cancellationToken = default)
    {
        var registration = registry.Get(workflowName, workflowVersion);
        if (registration.OutputType != typeof(TOutput))
        {
            throw new ArgumentException(
                $"Workflow '{workflowName}' version '{workflowVersion}' returns '{registration.OutputType}', not '{typeof(TOutput)}'.",
                nameof(TOutput));
        }
        var id = await StartAsync(workflowName, workflowVersion, input,
            workflowRunId, deadline, metadata, cancellationToken).ConfigureAwait(false);
        return new WorkflowHandle<TOutput>(this, id);
    }

    /// <summary>Creates a typed handle for an existing durable run.</summary>
    public WorkflowHandle<TOutput> GetHandle<TOutput>(Guid workflowRunId)
    {
        ThrowIfDisposed();
        return new WorkflowHandle<TOutput>(this, workflowRunId);
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
        ThrowIfDisposed();
        await leaseRecovery.EnsureInitializedAsync(cancellationToken)
            .ConfigureAwait(false);
        var diagnosticRun = await store.GetRunAsync(workflowRunId, cancellationToken)
            .ConfigureAwait(false);
        using var activity = ZhinuDiagnostics.StartWorkflowActivity(diagnosticRun);
        var started = timeProvider.GetTimestamp();
        ZhinuDiagnostics.RunsActiveCounter.Add(1);
        try
        {
            await executionPipeline.ExecuteAsync(workflowRunId, cancellationToken, 0)
                .ConfigureAwait(false);
            var run = await store.GetRunAsync(workflowRunId, CancellationToken.None)
                .ConfigureAwait(false);
            activity?.SetTag(
                ZhinuDiagnostics.Attributes.WorkflowStatus,
                run?.Status.ToString());
            activity?.SetStatus(ActivityStatusCode.Ok);
        }
        catch (Exception exception)
        {
            ZhinuDiagnostics.RecordException(activity, exception);
            throw;
        }
        finally
        {
            ZhinuDiagnostics.RunsActiveCounter.Add(-1);
            ZhinuDiagnostics.RunDurationHistogram.Record(
                timeProvider.GetElapsedTime(started).TotalSeconds);
        }
    }

    /// <summary>Recovers expired leases and executes every currently runnable run.</summary>
    public async Task<int> RunAvailableAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return await scanner.RunAvailableAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Persists cancellation and signals an active local execution.</summary>
    public async Task CancelAsync(
        Guid workflowRunId,
        CancellationToken cancellationToken = default) =>
        await CancelAsync(workflowRunId, actor: null, reason: null, cancellationToken)
            .ConfigureAwait(false);

    /// <summary>Persists cancellation with audit fields and signals an active local execution.</summary>
    public async Task CancelAsync(
        Guid workflowRunId,
        string? actor,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        await leaseRecovery.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        var previous = await store.GetRunAsync(workflowRunId, cancellationToken)
            .ConfigureAwait(false);
        await store.CancelRunAsync(
            workflowRunId,
            timeProvider.GetUtcNow(),
            cancellationToken).ConfigureAwait(false);
        if (previous?.Status != WorkflowStatus.Cancelled)
        {
            var current = await store.GetRunAsync(workflowRunId, cancellationToken)
                .ConfigureAwait(false);
            if (current?.Status == WorkflowStatus.Cancelled)
                ZhinuDiagnostics.RunsCancelledCounter.Add(1);
        }
        if (actor is not null || reason is not null)
        {
            logger.LogInformation(
                "Cancelled workflow {WorkflowRunId} by {Actor}: {Reason}",
                workflowRunId,
                actor ?? "unknown",
                reason ?? "no reason");
        }
        try
        {
            var subtree = await store.GetRunSubtreeAsync(
                workflowRunId, options.MaxNestingDepth, cancellationToken)
                .ConfigureAwait(false);
            foreach (var child in subtree)
            {
                if (child.Id == workflowRunId) continue;
                if (child.Status is WorkflowStatus.Completed or WorkflowStatus.Failed
                    or WorkflowStatus.Cancelled or WorkflowStatus.Compensated)
                    continue;
                await store.CancelRunAsync(child.Id, timeProvider.GetUtcNow(), cancellationToken)
                    .ConfigureAwait(false);
                NotifyEventAppended(child.Id);
                if (runningCancellations.TryGetValue(child.Id, out var childCancellation))
                    await childCancellation.CancelAsync().ConfigureAwait(false);
            }
        }
        catch { /* best-effort child cancellation */ }
        NotifyEventAppended(workflowRunId);
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
        await leaseRecovery.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        return await store.GetRunAsync(workflowRunId, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Queries persisted runs with optional filters and cursor pagination.</summary>
    public async Task<IReadOnlyList<WorkflowRun>> GetRunsAsync(
        RunQuery query,
        CancellationToken cancellationToken = default)
    {
        await leaseRecovery.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        return await store.GetRunsAsync(query, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Streams runs matching <paramref name="query"/> using cursor pagination.</summary>
    public async IAsyncEnumerable<WorkflowRun> EnumerateRunsAsync(
        RunQuery query,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var cursor = query;
        while (true)
        {
            var page = await GetRunsAsync(cursor, cancellationToken).ConfigureAwait(false);
            foreach (var run in page) yield return run;
            if (page.Count < cursor.Limit) yield break;
            cursor = cursor with { AfterId = page[^1].Id };
        }
    }

    /// <summary>Streams events for a run in sequence order.</summary>
    public async IAsyncEnumerable<WorkflowEvent> EnumerateEventsAsync(
        Guid workflowRunId,
        long afterSequence = 0,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var cursor = afterSequence;
        while (true)
        {
            var page = await GetEventsAsync(workflowRunId, cursor, 100, cancellationToken)
                .ConfigureAwait(false);
            foreach (var @event in page) yield return @event;
            if (page.Count == 0) yield break;
            cursor = page[^1].Sequence;
        }
    }

    /// <summary>Cancels all runs matching <paramref name="query"/> and returns the count cancelled.</summary>
    public async Task<int> CancelManyAsync(
        RunQuery query,
        string? actor = null,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        var count = 0;
        await foreach (var run in EnumerateRunsAsync(query, cancellationToken).ConfigureAwait(false))
        {
            if (run.Status is WorkflowStatus.Completed or WorkflowStatus.Failed
                or WorkflowStatus.Cancelled or WorkflowStatus.Compensated)
                continue;
            await CancelAsync(run.Id, actor, reason, cancellationToken).ConfigureAwait(false);
            count++;
        }
        return count;
    }

    /// <summary>Replaces a run's metadata without affecting its identity or contracts.</summary>
    public async Task<WorkflowRun?> UpdateRunMetadataAsync(
        Guid workflowRunId,
        object? metadata,
        CancellationToken cancellationToken = default)
    {
        await leaseRecovery.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
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
        await leaseRecovery.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        return await store.PurgeRunsAsync(
            olderThan,
            statuses,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves which steps a restart of <paramref name="stepKey"/> would
    /// invalidate under <paramref name="mode"/> without changing any state, so
    /// callers can inspect and confirm the effect before applying it. Returns
    /// the requested step followed by its invalidated dependents.
    /// </summary>
    public async Task<RestartPlan> PlanRestartAsync(
        Guid workflowRunId,
        string stepKey,
        StepRestartMode mode = StepRestartMode.Dependents,
        CancellationToken cancellationToken = default)
    {
        await leaseRecovery.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        return await store.PlanRestartAsync(
            workflowRunId,
            stepKey,
            mode,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns the durable compensations registered for a run, one per step
    /// revision, in creation order. Each row records the committed forward
    /// result it would undo and its own lifecycle status; compensations are
    /// persisted separately from step revisions so rollback history stays
    /// understandable.
    /// </summary>
    public async Task<IReadOnlyList<WorkflowStepCompensation>> GetCompensationsAsync(
        Guid workflowRunId,
        CancellationToken cancellationToken = default)
    {
        await leaseRecovery.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        return await store.GetCompensationsAsync(
            workflowRunId,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns the durable dependency edges recorded for a run, where each edge
    /// states that <see cref="StepDependency.StepKey"/> depends on
    /// <see cref="StepDependency.DependsOnStepKey"/>. The dependency graph is
    /// the basis for dependency-aware restarts.
    /// </summary>
    public async Task<IReadOnlyList<StepDependency>> GetDependencyGraphAsync(
        Guid workflowRunId,
        CancellationToken cancellationToken = default)
    {
        await leaseRecovery.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        return await store.GetStepDependenciesAsync(
            workflowRunId,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Transactionally restarts <paramref name="stepKey"/> under
    /// <paramref name="options"/>. Mode decides which steps are invalidated:
    /// <see cref="StepRestartMode.Dependents"/> (default) invalidates the step
    /// and its transitive durable dependents while reusing unrelated branches;
    /// <see cref="StepRestartMode.StepOnly"/> invalidates just the step;
    /// <see cref="StepRestartMode.CreationOrder"/> preserves the legacy
    /// creation-order behavior. Previous step revisions are preserved, the run
    /// is reset to <see cref="WorkflowStatus.Pending"/>, and the run's fencing
    /// generation is bumped so stale workers can no longer commit. If this
    /// process is currently executing the run, its execution is cancelled
    /// first (best-effort). Returns the plan that was applied.
    /// </summary>
    public async Task<RestartPlan> RestartStepAsync(
        Guid workflowRunId,
        string stepKey,
        RestartStepOptions options,
        CancellationToken cancellationToken = default)
    {
        await leaseRecovery.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        if (runningCancellations.TryGetValue(
                workflowRunId,
                out var runningCancellation))
        {
            await runningCancellation.CancelAsync().ConfigureAwait(false);
        }
        var plan = await store.RestartStepAsync(
            workflowRunId,
            stepKey,
            options.Mode,
            options.Actor,
            options.Reason,
            timeProvider.GetUtcNow(),
            cancellationToken).ConfigureAwait(false);
        logger.LogInformation(
            "Restarted step '{StepKey}' of workflow {WorkflowRunId} ({Mode}); " +
            "invalidated {InvalidatedCount} step(s).",
            stepKey,
            workflowRunId,
            options.Mode,
            plan.StepsToInvalidate.Count);
        return plan;
    }

    /// <summary>Restarts <paramref name="stepKey"/> with dependency-aware defaults.</summary>
    public Task<RestartPlan> RestartStepAsync(
        Guid workflowRunId,
        string stepKey,
        CancellationToken cancellationToken = default) =>
        RestartStepAsync(
            workflowRunId,
            stepKey,
            new RestartStepOptions(),
            cancellationToken);

    /// <summary>
    /// Previews a new run seeded from completed source steps. The source run is
    /// not changed and no new run is created.
    /// </summary>
    public async Task<ForkPlan> PlanForkAsync(
        Guid sourceWorkflowRunId,
        string targetStepKey,
        StepRestartMode mode = StepRestartMode.Dependents,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(targetStepKey);
        await leaseRecovery.EnsureInitializedAsync(cancellationToken)
            .ConfigureAwait(false);
        return await store.PlanForkAsync(
            sourceWorkflowRunId,
            targetStepKey,
            mode,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Atomically creates a pending run with the same workflow contract and
    /// input as <paramref name="sourceWorkflowRunId"/>. Completed source steps
    /// outside the selected restart boundary are copied as reusable results;
    /// the selected step, its invalidated dependents, and incomplete steps run
    /// normally under the new identity. The source run is never modified.
    /// </summary>
    public async Task<Guid> ForkAsync(
        Guid sourceWorkflowRunId,
        string targetStepKey,
        ForkRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(targetStepKey);
        options ??= new ForkRunOptions();
        await leaseRecovery.EnsureInitializedAsync(cancellationToken)
            .ConfigureAwait(false);
        var source = await store.GetRunAsync(sourceWorkflowRunId, cancellationToken)
            .ConfigureAwait(false) ??
            throw new KeyNotFoundException(
                $"Workflow '{sourceWorkflowRunId:D}' does not exist.");
        var registration = registry.Get(source.WorkflowName, source.WorkflowVersion);
        if (source.InputType != SerializationIdentity.TypeId(registration.InputType) ||
            source.OutputType != SerializationIdentity.TypeId(registration.OutputType))
        {
            throw new WorkflowStateException(
                "The source run's serialized contract does not match the registered workflow.");
        }
        var id = options.WorkflowRunId ?? Guid.NewGuid();
        var now = timeProvider.GetUtcNow();
        var newRun = new WorkflowRun
        {
            Id = id,
            WorkflowName = source.WorkflowName,
            WorkflowVersion = source.WorkflowVersion,
            Status = WorkflowStatus.Pending,
            InputJson = source.InputJson,
            InputType = source.InputType,
            OutputType = source.OutputType,
            CreatedAt = now,
            UpdatedAt = now,
            Deadline = options.Deadline,
            MetadataJson = source.MetadataJson,
            SourceRunId = sourceWorkflowRunId,
            TraceId = (Activity.Current?.TraceId ?? ActivityTraceId.CreateRandom())
                .ToHexString()
        };
        var plan = await store.ForkRunAsync(
            sourceWorkflowRunId,
            newRun,
            targetStepKey,
            options.Mode,
            options.Actor,
            options.Reason,
            cancellationToken).ConfigureAwait(false);
        logger.LogInformation(
            "Forked workflow {SourceWorkflowRunId} into {WorkflowRunId} from " +
            "step '{StepKey}'; reused {ReusedCount} step(s).",
            sourceWorkflowRunId,
            id,
            targetStepKey,
            plan.StepsToReuse.Count);
        ZhinuDiagnostics.RunsStartedCounter.Add(
            1,
            new KeyValuePair<string, object?>(
                ZhinuDiagnostics.Attributes.WorkflowName,
                source.WorkflowName),
            new KeyValuePair<string, object?>(
                ZhinuDiagnostics.Attributes.WorkflowVersion,
                source.WorkflowVersion));
        return id;
    }

    /// <summary>Creates a typed handle for a forked pending run.</summary>
    public async Task<WorkflowHandle<TOutput>> ForkHandleAsync<TOutput>(
        Guid sourceWorkflowRunId,
        string targetStepKey,
        ForkRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await leaseRecovery.EnsureInitializedAsync(cancellationToken)
            .ConfigureAwait(false);
        var source = await store.GetRunAsync(sourceWorkflowRunId, cancellationToken)
            .ConfigureAwait(false) ??
            throw new KeyNotFoundException(
                $"Workflow '{sourceWorkflowRunId:D}' does not exist.");
        var registration = registry.Get(source.WorkflowName, source.WorkflowVersion);
        if (registration.OutputType != typeof(TOutput))
        {
            throw new ArgumentException(
                $"Workflow '{source.WorkflowName}' version '{source.WorkflowVersion}' " +
                $"returns '{registration.OutputType}', not '{typeof(TOutput)}'.",
                nameof(TOutput));
        }
        var id = await ForkAsync(
            sourceWorkflowRunId,
            targetStepKey,
            options,
            cancellationToken).ConfigureAwait(false);
        return new WorkflowHandle<TOutput>(this, id);
    }

    /// <summary>
    /// Resolves which steps a full rollback of <paramref name="workflowRunId"/>
    /// would compensate without changing any state: every step with a committed
    /// forward result and a claimable compensation, in reverse dependency order.
    /// </summary>
    public async Task<RollbackPlan> PlanRollbackAsync(
        Guid workflowRunId,
        CancellationToken cancellationToken = default)
    {
        await leaseRecovery.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        return await store.PlanRollbackAsync(
            workflowRunId,
            null,
            RollbackBoundary.AfterStep,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves which steps rolling back <paramref name="workflowRunId"/> to
    /// <paramref name="targetStepKey"/> would compensate, without changing any
    /// state. <see cref="RollbackBoundary.BeforeStep"/> includes the target
    /// itself; <see cref="RollbackBoundary.AfterStep"/> preserves it. Each plan
    /// entry states what would happen to the step and why (boundary, dependent,
    /// ancestor, or independent branch).
    /// </summary>
    public async Task<RollbackPlan> PlanRollbackAsync(
        Guid workflowRunId,
        string targetStepKey,
        RollbackOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetStepKey);
        ArgumentNullException.ThrowIfNull(options);
        await leaseRecovery.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        return await store.PlanRollbackAsync(
            workflowRunId,
            targetStepKey,
            options.Boundary,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Plans a rollback to a step with <c>AfterStep</c> boundary defaults.</summary>
    public Task<RollbackPlan> PlanRollbackAsync(
        Guid workflowRunId,
        string targetStepKey,
        CancellationToken cancellationToken = default) =>
        PlanRollbackAsync(
            workflowRunId,
            targetStepKey,
            new RollbackOptions(RollbackBoundary.AfterStep),
            cancellationToken);

    /// <summary>
    /// Rolls back a completed or failed run by compensating every successfully
    /// completed compensatable forward operation in reverse dependency order.
    /// On success the run reaches <see cref="WorkflowStatus.Compensated"/>;
    /// a compensation that exhausts its retries fails the run, which stays
    /// claimable by a later rollback attempt. Already-completed compensations
    /// are reused, so rollback is safe to repeat (at-least-once).
    /// </summary>
    public async Task RollbackAsync(
        Guid workflowRunId,
        string? actor = null,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        await leaseRecovery.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await rollbackCoordinator.RollbackAsync(
            workflowRunId,
            null,
            RollbackBoundary.AfterStep,
            actor,
            reason,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Rolls back to <paramref name="stepKey"/>.
    /// <see cref="RollbackBoundary.AfterStep"/> leaves the target's committed
    /// operation intact and compensates only its transitive dependents;
    /// <see cref="RollbackBoundary.BeforeStep"/> compensates the target too.
    /// </summary>
    public async Task RollbackToStepAsync(
        Guid workflowRunId,
        string stepKey,
        RollbackBoundary boundary,
        string? actor = null,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stepKey);
        await leaseRecovery.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await rollbackCoordinator.RollbackAsync(
            workflowRunId,
            stepKey,
            boundary,
            actor,
            reason,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Rolls a run all the way back (compensating every step that has a
    /// claimable compensation), then rewinds it to a re-executable state and
    /// lets it run forward again. The rollback-and-restart work is durable:
    /// should the process die mid-operation, a later
    /// <see cref="ExecuteAsync"/> call resumes it.
    /// </summary>
    public async Task RollbackAndRestartAsync(
        Guid workflowRunId,
        string? actor = null,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        await leaseRecovery.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await rollbackAndRestart.RollbackAndRestartAsync(
            workflowRunId,
            actor,
            reason,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Buffers a typed external signal for <paramref name="workflowRunId"/>.
    /// </summary>
    public Task SendSignalAsync<TPayload>(
        Guid workflowRunId,
        SignalDefinition<TPayload> signal,
        TPayload? data = default,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(signal);
        return SendSignalAsync(workflowRunId, signal.Name, data, cancellationToken);
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
        await leaseRecovery.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
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
        await leaseRecovery.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        return await store.GetStepsAsync(workflowRunId, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Returns every durable artifact reference published by a run, including
    /// artifacts produced before a later step or workflow failure.
    /// </summary>
    public async Task<IReadOnlyList<WorkflowArtifactReference>> GetArtifactsAsync(
        Guid workflowRunId,
        CancellationToken cancellationToken = default)
    {
        await leaseRecovery.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        return await store.GetArtifactsAsync(workflowRunId, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Filters and pages artifact references belonging to a run.</summary>
    public async Task<IReadOnlyList<WorkflowArtifactReference>> QueryArtifactsAsync(
        Guid workflowRunId,
        ArtifactQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        query.Validate();
        await leaseRecovery.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        return await store.QueryArtifactsAsync(workflowRunId, query, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Streams artifact references for a run using offset pagination.</summary>
    public async IAsyncEnumerable<WorkflowArtifactReference> EnumerateArtifactsAsync(
        Guid workflowRunId,
        ArtifactQuery? query = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var baseQuery = query ?? new ArtifactQuery();
        var offset = baseQuery.Offset;
        while (true)
        {
            var pageQuery = new ArtifactQuery
            {
                Name = baseQuery.Name,
                ArtifactType = baseQuery.ArtifactType,
                ProducerStepKey = baseQuery.ProducerStepKey,
                LatestOnly = baseQuery.LatestOnly,
                Offset = offset,
                Limit = baseQuery.Limit
            };
            var page = await QueryArtifactsAsync(workflowRunId, pageQuery, cancellationToken)
                .ConfigureAwait(false);
            foreach (var artifact in page) yield return artifact;
            if (page.Count < baseQuery.Limit) yield break;
            offset += page.Count;
        }
    }

    /// <summary>Returns the newest revision of a named artifact in a run.</summary>
    public async Task<WorkflowArtifactReference?> GetLatestArtifactAsync(
        Guid workflowRunId,
        string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        await leaseRecovery.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        return await store.GetLatestArtifactAsync(workflowRunId, name, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Gets an immutable artifact reference by its globally unique id.</summary>
    public async Task<WorkflowArtifactReference?> GetArtifactAsync(
        Guid artifactId,
        CancellationToken cancellationToken = default)
    {
        await leaseRecovery.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        return await store.GetArtifactAsync(artifactId, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<WorkflowEvent>> GetEventsAsync(
        Guid workflowRunId,
        long afterSequence = 0,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        await leaseRecovery.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
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
        await leaseRecovery.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// Explains why a run is terminal, executable, waiting, blocked, leased,
    /// or unable to continue. Returns null when the run does not exist.
    /// </summary>
    public async Task<RunDiagnosis?> DiagnoseAsync(
        Guid workflowRunId,
        CancellationToken cancellationToken = default)
    {
        await leaseRecovery.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        var run = await store.GetRunAsync(workflowRunId, cancellationToken)
            .ConfigureAwait(false);
        if (run is null)
            return null;
        var steps = await store.GetStepsAsync(workflowRunId, cancellationToken)
            .ConfigureAwait(false);
        var dependencies = await store.GetStepDependenciesAsync(
            workflowRunId, cancellationToken).ConfigureAwait(false);
        var operation = await store.GetActiveOperationAsync(
            workflowRunId, cancellationToken).ConfigureAwait(false);
        return Diagnose(run, steps, dependencies, operation);
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
        var artifacts = options.IncludeArtifacts
            ? await store.GetArtifactsAsync(run.Id, cancellationToken).ConfigureAwait(false)
            : [];
        var operation = options.IncludeDiagnosis || options.IncludeActiveOperation
            ? await store.GetActiveOperationAsync(run.Id, cancellationToken).ConfigureAwait(false)
            : null;
        RunDiagnosis? diagnosis = null;
        if (options.IncludeDiagnosis)
        {
            var dependencies = await store.GetStepDependenciesAsync(
                run.Id, cancellationToken).ConfigureAwait(false);
            diagnosis = Diagnose(run, steps, dependencies, operation);
        }
        var sourceLineage = options.IncludeSourceLineage
            ? await GetSourceLineageAsync(
                run.SourceRunId,
                options.SourceLineageMaxDepth,
                cancellationToken).ConfigureAwait(false)
            : [];
        return new WorkflowRunProgress
        {
            Run = run,
            Steps = steps,
            Events = events,
            Artifacts = artifacts,
            ActiveOperation = operation,
            Diagnosis = diagnosis,
            SourceRun = sourceLineage.FirstOrDefault(),
            SourceLineage = sourceLineage
        };
    }

    private async Task<IReadOnlyList<WorkflowRun>> GetSourceLineageAsync(
        Guid? sourceRunId,
        int maxDepth,
        CancellationToken cancellationToken)
    {
        var lineage = new List<WorkflowRun>();
        var visited = new HashSet<Guid>();
        while (sourceRunId is { } id &&
            lineage.Count < maxDepth &&
            visited.Add(id))
        {
            var source = await store.GetRunAsync(id, cancellationToken).ConfigureAwait(false);
            if (source is null)
                break;
            lineage.Add(source);
            sourceRunId = source.SourceRunId;
        }
        return lineage;
    }

    private RunDiagnosis Diagnose(
        WorkflowRun run,
        IReadOnlyList<WorkflowStepRun> steps,
        IReadOnlyList<StepDependency> dependencies,
        WorkflowRunOperation? operation)
    {
        RunDiagnosis Result(
            RunDiagnosisCode code,
            string summary,
            WorkflowStepRun? step = null,
            DateTimeOffset? until = null,
            IReadOnlyList<string>? blocking = null) => new()
            {
                WorkflowRunId = run.Id,
                Code = code,
                Summary = summary,
                StepKey = step?.StepKey,
                Until = until,
                LeaseOwner = step?.LeaseOwner ?? run.LeaseOwner,
                Operation = operation,
                BlockingStepKeys = blocking ?? []
            };

        var terminal = run.Status is WorkflowStatus.Completed or
            WorkflowStatus.Cancelled or WorkflowStatus.Compensated;
        if (terminal)
            return Result(RunDiagnosisCode.Terminal, $"Run is terminal in state '{run.Status}'.");
        var now = timeProvider.GetUtcNow();
        var failed = steps.FirstOrDefault(step => step.Status == StepStatus.Failed);
        if (failed is not null)
        {
            return Result(
                RunDiagnosisCode.PermanentlyFailedStep,
                $"Step '{failed.StepKey}' failed permanently after attempt {failed.Attempt}.",
                failed);
        }
        if (run.Status == WorkflowStatus.Failed)
            return Result(RunDiagnosisCode.Terminal, "Run is terminal in state 'Failed'.");
        if (operation is not null || run.Status == WorkflowStatus.RollingBack)
        {
            return Result(
                RunDiagnosisCode.ActiveOperation,
                operation is null
                    ? "The run is rolling back and awaits operation recovery."
                    : $"Operation '{operation.OperationType}' is in phase '{operation.Status}'.");
        }
        if (!registry.TryGet(run.WorkflowName, run.WorkflowVersion, out _))
        {
            return Result(
                RunDiagnosisCode.MissingWorkflowRegistration,
                $"Workflow '{run.WorkflowName}' version '{run.WorkflowVersion}' is not registered.");
        }
        if (run.Deadline is { } deadline && deadline <= now)
            return Result(RunDiagnosisCode.DeadlineExceeded, "The workflow deadline has passed.", until: deadline);
        var signal = steps.FirstOrDefault(step =>
            step.Status == StepStatus.Waiting && step.SignalName is not null);
        if (signal is not null)
        {
            return Result(
                RunDiagnosisCode.WaitingForSignal,
                $"Step '{signal.StepKey}' is waiting for signal '{signal.SignalName}'.",
                signal);
        }
        var timedWait = steps
            .Where(step => step.Status == StepStatus.Waiting && step.AvailableAt > now)
            .OrderBy(step => step.AvailableAt)
            .FirstOrDefault();
        if (timedWait is not null)
        {
            var retry = timedWait.Error is not null;
            return Result(
                retry ? RunDiagnosisCode.WaitingForRetry : RunDiagnosisCode.WaitingForDelay,
                retry
                    ? $"Step '{timedWait.StepKey}' is waiting for its retry time."
                    : $"Step '{timedWait.StepKey}' is waiting for its durable delay.",
                timedWait,
                timedWait.AvailableAt);
        }
        var completedKeys = steps
            .Where(step => step.Status == StepStatus.Completed)
            .Select(step => step.StepKey)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var pending in steps.Where(step => step.Status == StepStatus.Pending))
        {
            var blockers = dependencies
                .Where(edge => edge.StepKey == pending.StepKey &&
                    !completedKeys.Contains(edge.DependsOnStepKey))
                .Select(edge => edge.DependsOnStepKey)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            if (blockers.Length > 0)
            {
                return Result(
                    RunDiagnosisCode.BlockedByDependencies,
                    $"Step '{pending.StepKey}' is blocked by {blockers.Length} incomplete dependency step(s).",
                    pending,
                    blocking: blockers);
            }
        }
        var leasedStep = steps.FirstOrDefault(step =>
            step.Status == StepStatus.Running && step.LeaseExpiresAt > now);
        if (leasedStep is not null || run.LeaseExpiresAt > now)
        {
            return Result(
                RunDiagnosisCode.Executing,
                leasedStep is null
                    ? "The run is actively leased by a worker."
                    : $"Step '{leasedStep.StepKey}' is actively leased by a worker.",
                leasedStep,
                leasedStep?.LeaseExpiresAt ?? run.LeaseExpiresAt);
        }
        var expiredStep = steps.FirstOrDefault(step =>
            step.Status == StepStatus.Running &&
            (step.LeaseExpiresAt is null || step.LeaseExpiresAt <= now));
        if (expiredStep is not null ||
            run.Status == WorkflowStatus.Running &&
            (run.LeaseExpiresAt is null || run.LeaseExpiresAt <= now))
        {
            return Result(
                RunDiagnosisCode.ExpiredLeaseAwaitingRecovery,
                expiredStep is null
                    ? "The run lease expired and awaits recovery."
                    : $"Step '{expiredStep.StepKey}' has an expired lease and awaits recovery.",
                expiredStep);
        }
        if (run.Status == WorkflowStatus.Pending)
            return Result(RunDiagnosisCode.ReadyToExecute, "The run is pending and ready for a worker.");
        return Result(RunDiagnosisCode.AwaitingWorker, "The run has no active lease and awaits a worker.");
    }

    /// <summary>Polls durable state without blocking a thread until a run terminates.</summary>
    public async Task<TOutput> WaitForCompletionAsync<TOutput>(
        Guid workflowRunId,
        DateTimeOffset? deadline = null,
        CancellationToken cancellationToken = default)
    {
        await leaseRecovery.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
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
                case WorkflowStatus.Compensated:
                    throw new WorkflowStateException(
                        $"Workflow '{workflowRunId:D}' was compensated and has no forward result to return.");
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
            if (run is null || RunExecutionPipeline.IsTerminal(run.Status) && events.Count == 0)
            {
                eventChannels.TryRemove(workflowRunId, out _);
                yield break;
            }
            if (events.Count == 0)
            {
                // Wait briefly for an in-process notification instead of
                // hammering the store every poll interval. Fall back to the
                // poll interval so events appended by other processes (or
                // before this subscriber existed) are still observed.
                var channel = eventChannels.GetOrAdd(
                    workflowRunId,
                    _ => Channel.CreateBounded<byte>(
                        new BoundedChannelOptions(1)
                        {
                            FullMode = BoundedChannelFullMode.DropWrite
                        }));
                var notify = channel.Reader.WaitToReadAsync(cancellationToken)
                    .AsTask();
                var poll = Task.Delay(
                    options.PollInterval,
                    timeProvider,
                    cancellationToken);
                await Task.WhenAny(notify, poll).ConfigureAwait(false);
            }
        }
    }

    private void NotifyEventAppended(Guid workflowRunId)
    {
        if (eventChannels.TryGetValue(workflowRunId, out var channel))
        {
            channel.Writer.TryWrite(0);
        }
    }

    /// <summary>Returns a non-throwing point-in-time result for a run.</summary>
    public async Task<WorkflowResult<TOutput>> GetResultAsync<TOutput>(
        Guid workflowRunId,
        CancellationToken cancellationToken = default)
    {
        var run = await GetRunAsync(workflowRunId, cancellationToken)
            .ConfigureAwait(false) ?? throw new KeyNotFoundException(
                $"Workflow '{workflowRunId:D}' does not exist.");
        TOutput? value = default;
        if (run.Status == WorkflowStatus.Completed)
        {
            var expectedType = SerializationIdentity.TypeId(typeof(TOutput));
            if (!string.Equals(run.OutputType, expectedType, StringComparison.Ordinal))
            {
                throw new WorkflowSerializationException(
                    $"Workflow result was stored as '{run.OutputType}', not '{expectedType}'.");
            }
            try
            {
                value = JsonSerializer.Deserialize<TOutput>(
                    run.OutputJson ?? "null", serializerOptions);
            }
            catch (JsonException exception)
            {
                throw new WorkflowSerializationException(
                    $"Workflow result could not be deserialized as '{expectedType}'.",
                    exception);
            }
        }
        return new WorkflowResult<TOutput>
        {
            WorkflowRunId = workflowRunId,
            Status = run.Status,
            Value = value,
            Error = run.Error
        };
    }

    /// <summary>Cancels local executions and closes local subscriptions.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;
        foreach (var cancellation in runningCancellations.Values)
            await cancellation.CancelAsync().ConfigureAwait(false);
        var settleDeadline = timeProvider.GetUtcNow() + options.LeaseDuration;
        while (!runningCancellations.IsEmpty &&
               timeProvider.GetUtcNow() < settleDeadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), timeProvider)
                .ConfigureAwait(false);
        }
        foreach (var channel in eventChannels.Values)
            channel.Writer.TryComplete();
        eventChannels.Clear();
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(disposed != 0, this);

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
