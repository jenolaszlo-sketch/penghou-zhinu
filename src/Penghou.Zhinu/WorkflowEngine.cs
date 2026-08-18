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
        var leaseGeneration = await store.TryClaimRunAsync(
                workflowRunId,
                ownerId,
                now,
                now + options.LeaseDuration,
                cancellationToken).ConfigureAwait(false);
        if (leaseGeneration is null)
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
                leaseGeneration.Value,
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
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
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
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
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
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
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
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
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
    /// Resolves which steps a full rollback of <paramref name="workflowRunId"/>
    /// would compensate without changing any state: every step with a committed
    /// forward result and a claimable compensation, in reverse dependency order.
    /// </summary>
    public async Task<RollbackPlan> PlanRollbackAsync(
        Guid workflowRunId,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
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
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
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
    public Task RollbackAsync(
        Guid workflowRunId,
        string? actor = null,
        string? reason = null,
        CancellationToken cancellationToken = default) =>
        RollbackCoreAsync(
            workflowRunId,
            null,
            RollbackBoundary.AfterStep,
            actor,
            reason,
            cancellationToken);

    /// <summary>
    /// Rolls back to <paramref name="stepKey"/>.
    /// <see cref="RollbackBoundary.AfterStep"/> leaves the target's committed
    /// operation intact and compensates only its transitive dependents;
    /// <see cref="RollbackBoundary.BeforeStep"/> compensates the target too.
    /// </summary>
    public Task RollbackToStepAsync(
        Guid workflowRunId,
        string stepKey,
        RollbackBoundary boundary,
        string? actor = null,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stepKey);
        return RollbackCoreAsync(
            workflowRunId,
            stepKey,
            boundary,
            actor,
            reason,
            cancellationToken);
    }

    private async Task RollbackCoreAsync(
        Guid workflowRunId,
        string? targetStepKey,
        RollbackBoundary boundary,
        string? actor,
        string? reason,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        var run = await store.GetRunAsync(
            workflowRunId,
            cancellationToken).ConfigureAwait(false) ??
            throw new KeyNotFoundException(
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
            if (compensateKeys.Count > 0)
            {
                var steps = (await store.GetStepsAsync(
                    workflowRunId,
                    cancellationToken).ConfigureAwait(false))
                    .ToDictionary(
                        step => step.StepKey,
                        StringComparer.Ordinal);
                var rows = await store.GetCompensationsAsync(
                    workflowRunId,
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

                if (byKey.Count > 0)
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
                    var context = new WorkflowContext(
                        workflowRunId,
                        store,
                        ownerId,
                        options,
                        serializerOptions,
                        timeProvider,
                        generation.Value,
                        cancellationToken,
                        eventPublisher,
                        executeChildRun: null,
                        replaySteps: steps,
                        rollbackCompensations: byKey);
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
                        await ExecuteCompensationAsync(
                            invocation,
                            generation.Value,
                            actor,
                            reason,
                            cancellationToken).ConfigureAwait(false);
                    }
                }
            }

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
            logger.LogInformation(
                "Compensated workflow {WorkflowRunId} ({CompensatedCount} step(s)).",
                workflowRunId,
                compensateKeys.Count);
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

    private async Task ExecuteCompensationAsync(
        WorkflowContext.CompensationInvocation invocation,
        long generation,
        string? actor,
        string? reason,
        CancellationToken cancellationToken)
    {
        var compensation = invocation.Compensation;
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

            await store.AppendEventAsync(
                claim.WorkflowRunId,
                WorkflowEventTypes.CompensationStarted,
                null,
                claim.StepKey,
                claim.Attempt,
                cancellationToken).ConfigureAwait(false);

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
                return;
            }
            catch (OperationCanceledException) when (
                timeoutCancellation?.IsCancellationRequested == true &&
                !cancellationToken.IsCancellationRequested)
            {
                var timeout = new TimeoutException(
                    $"Compensation for step '{claim.StepKey}' exceeded its execution timeout.");
                var retryAt = ScheduleCompensationRetry(claim, retryPolicy);
                await RecordCompensationFailureAsync(
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
                var retryAt = ScheduleCompensationRetry(claim, retryPolicy);
                await RecordCompensationFailureAsync(
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

    private DateTimeOffset? ScheduleCompensationRetry(
        WorkflowStepCompensation claim,
        RetryPolicy retryPolicy) =>
        claim.Attempt < retryPolicy.MaxAttempts
            ? timeProvider.GetUtcNow() + retryPolicy.DelayAfter(claim.Attempt)
            : (DateTimeOffset?)null;

    private async Task RecordCompensationFailureAsync(
        WorkflowStepCompensation claim,
        Exception exception,
        DateTimeOffset? retryAt)
    {
        var now = timeProvider.GetUtcNow();
        var error = WorkflowError.FromException(exception, now, claim.Attempt);
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
            WorkflowStatus.Cancelled or WorkflowStatus.Compensated;

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
