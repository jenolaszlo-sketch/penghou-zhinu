namespace Penghou.Zhinu;

public sealed partial class WorkflowContext
{
    /// <summary>
    /// Executes a durable, sequential state loop. The continuation condition is
    /// evaluated before each body execution and its decision is persisted. Each
    /// successful body produces an immutable state value that is committed at a
    /// fenced iteration boundary. Replays reuse completed condition, body, and
    /// state-commit steps.
    /// </summary>
    /// <remarks>
    /// The body must perform durable work through the supplied
    /// <see cref="WorkflowLoopIteration{TState}"/>. Independent collection work
    /// belongs in <see cref="FanOutAsync{TInput,TOutput}(string,IReadOnlyList{TInput},Func{TInput,WorkflowStepContext,CancellationToken,Task{TOutput}},StepOptions?,CancellationToken)"/>.
    /// </remarks>
    public Task<TState> LoopAsync<TState>(
        string loopKey,
        TState initialState,
        Func<TState, bool> continueWhile,
        Func<WorkflowLoopIteration<TState>, CancellationToken, Task<LoopBodyOutcome<TState>>> body,
        LoopOptions options,
        CancellationToken cancellationToken = default) =>
        LoopCoreAsync(
            DurableLoopScope.Root(loopKey),
            initialState,
            continueWhile,
            body,
            options,
            entryDependencyStepKey: null,
            cancellationToken);

    internal async Task<TState> LoopCoreAsync<TState>(
        DurableLoopScope scope,
        TState initialState,
        Func<TState, bool> continueWhile,
        Func<WorkflowLoopIteration<TState>, CancellationToken, Task<LoopBodyOutcome<TState>>> body,
        LoopOptions loopOptions,
        string? entryDependencyStepKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(continueWhile);
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(loopOptions);
        if (scope.Depth > options.MaxLoopNestingDepth)
        {
            throw new LoopNestingLimitExceededException(
                scope.DisplayPath,
                scope.Depth,
                options.MaxLoopNestingDepth);
        }

        IReadOnlyList<WorkflowStepRun> existingSteps = IsRollback
            ? replaySteps!.Values.ToArray()
            : await store.GetStepsAsync(
                WorkflowRunId,
                cancellationToken).ConfigureAwait(false);
        IReadOnlyDictionary<string, WorkflowStepRun> completedSteps = IsRollback
            ? new Dictionary<string, WorkflowStepRun>(StringComparer.Ordinal)
            : existingSteps
                .Where(step => step.Status == StepStatus.Completed)
                .ToDictionary(step => step.StepKey, StringComparer.Ordinal);
        var resolvedLimits = await ResolveLoopLimitsAsync(
            scope,
            loopOptions,
            existingSteps.SingleOrDefault(step =>
                step.StepKey == DurableLoopStepKeys.Limits(scope)),
            entryDependencyStepKey,
            cancellationToken).ConfigureAwait(false);
        var loopEntryDependency = resolvedLimits.EffectiveDeadline is not null
            ? DurableLoopStepKeys.Limits(scope)
            : entryDependencyStepKey;
        var state = initialState;
        string? previousCommitStepKey = null;
        for (var iterationNumber = 1; ; iterationNumber++)
        {
            var iteration = scope.Iteration(iterationNumber);
            var conditionStepKey = DurableLoopStepKeys.Condition(iteration);
            var conditionDependency = previousCommitStepKey ?? loopEntryDependency;
            if (!completedSteps.ContainsKey(conditionStepKey))
            {
                await EnsureTimeLimitNotExceededAsync(
                    scope,
                    resolvedLimits,
                    conditionDependency,
                    cancellationToken).ConfigureAwait(false);
            }
            var conditionOptions = conditionDependency is null
                ? null
                : new StepOptions { DependsOn = [conditionDependency] };
            var shouldContinue = await StepAsync(
                conditionStepKey,
                new LoopConditionInput<TState>(state, loopOptions.MaxIterations),
                (value, _) => Task.FromResult(continueWhile(value.State)),
                conditionOptions,
                cancellationToken).ConfigureAwait(false);

            if (!shouldContinue)
            {
                return await CompleteLoopAsync(
                    scope,
                    state,
                    iterationNumber - 1,
                    LoopCompletionReason.ConditionFalse,
                    conditionStepKey,
                    cancellationToken).ConfigureAwait(false);
            }

            if (iterationNumber > loopOptions.MaxIterations)
            {
                await FailLoopLimitAsync(
                    scope,
                    new LoopLimitExceededEvent(
                        scope.DisplayPath,
                        LoopLimitKind.IterationCount,
                        loopOptions.MaxIterations,
                        null,
                        null),
                    [conditionStepKey],
                    cancellationToken).ConfigureAwait(false);
            }

            var commitStepKey = DurableLoopStepKeys.Commit(iteration);
            if (completedSteps.TryGetValue(commitStepKey, out var completedCommit))
            {
                var committed = Deserialize<LoopCommittedOutcome<TState>>(
                    completedCommit.OutputJson,
                    SerializationIdentity.TypeId(typeof(LoopCommittedOutcome<TState>)));
                state = committed.State;
                previousCommitStepKey = commitStepKey;
                if (committed.Kind == LoopBodyOutcomeKind.Break)
                {
                    return await CompleteLoopAsync(
                        scope,
                        state,
                        iterationNumber,
                        LoopCompletionReason.Break,
                        commitStepKey,
                        cancellationToken).ConfigureAwait(false);
                }
                continue;
            }

            await EnsureTimeLimitNotExceededAsync(
                scope,
                resolvedLimits,
                conditionStepKey,
                cancellationToken).ConfigureAwait(false);

            var loopIteration = new WorkflowLoopIteration<TState>(
                this,
                iteration,
                state,
                conditionStepKey);
            var outcome = await body(loopIteration, cancellationToken)
                .ConfigureAwait(false);
            loopIteration.ValidateOutcome(outcome);
            var pendingCommit = new LoopCommittedOutcome<TState>(
                outcome.Kind,
                outcome.State);
            var commitDependencies = new HashSet<string>(
                loopIteration.GetBodyStepKeys(),
                StringComparer.Ordinal)
            {
                conditionStepKey
            };
            await EnsureTimeLimitNotExceededAsync(
                scope,
                resolvedLimits,
                commitDependencies,
                cancellationToken).ConfigureAwait(false);
            var committedOutcome = await StepAsync(
                commitStepKey,
                pendingCommit,
                async (value, step, token) =>
                {
                    await step.EmitAsync(
                        WorkflowEventTypes.LoopIterationCommitted,
                        new LoopIterationCommittedEvent(
                            scope.DisplayPath,
                            iterationNumber,
                            value.Kind),
                        token).ConfigureAwait(false);
                    return value;
                },
                new StepOptions { DependsOn = commitDependencies },
                cancellationToken).ConfigureAwait(false);
            state = committedOutcome.State;
            previousCommitStepKey = commitStepKey;
            if (committedOutcome.Kind == LoopBodyOutcomeKind.Break)
            {
                return await CompleteLoopAsync(
                    scope,
                    state,
                    iterationNumber,
                    LoopCompletionReason.Break,
                    commitStepKey,
                    cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<ResolvedLoopLimits> ResolveLoopLimitsAsync(
        DurableLoopScope scope,
        LoopOptions loopOptions,
        WorkflowStepRun? existingLimitsStep,
        string? entryDependencyStepKey,
        CancellationToken cancellationToken)
    {
        if (loopOptions.Deadline is null && loopOptions.TimeBudget is null)
            return ResolvedLoopLimits.None;

        var configured = new ConfiguredLoopLimits(
            loopOptions.MaxIterations,
            loopOptions.Deadline,
            loopOptions.TimeBudget);
        var resolution = new LoopLimitResolution(
            configured,
            timeProvider.GetUtcNow());
        if (existingLimitsStep is not null &&
            existingLimitsStep.Status != StepStatus.Pending)
        {
            var storedResolution = Deserialize<LoopLimitResolution>(
                existingLimitsStep.InputJson,
                SerializationIdentity.TypeId(typeof(LoopLimitResolution)));
            if (storedResolution.Configuration != configured)
            {
                throw new WorkflowStateException(
                    $"Loop '{scope.DisplayPath}' limit configuration changed after its durable boundary was established.");
            }
            resolution = storedResolution;
        }
        var stepOptions = entryDependencyStepKey is null
            ? null
            : new StepOptions { DependsOn = [entryDependencyStepKey] };
        return await StepAsync(
            DurableLoopStepKeys.Limits(scope),
            resolution,
            (value, _, _) => Task.FromResult(ResolveLoopLimits(value)),
            stepOptions,
            cancellationToken).ConfigureAwait(false);
    }

    private static ResolvedLoopLimits ResolveLoopLimits(LoopLimitResolution resolution)
    {
        var configured = resolution.Configuration;
        DateTimeOffset? effectiveDeadline = configured.Deadline;
        var limitKind = configured.Deadline is null
            ? (LoopLimitKind?)null
            : LoopLimitKind.Deadline;
        if (configured.TimeBudget is { } timeBudget)
        {
            DateTimeOffset budgetDeadline;
            try
            {
                budgetDeadline = resolution.BudgetStartedAt.Add(timeBudget);
            }
            catch (ArgumentOutOfRangeException exception)
            {
                throw new WorkflowConfigurationException(
                    "The durable loop time budget exceeds the supported timestamp range.",
                    exception);
            }

            if (effectiveDeadline is null || budgetDeadline < effectiveDeadline)
            {
                effectiveDeadline = budgetDeadline;
                limitKind = LoopLimitKind.TimeBudget;
            }
        }

        return new ResolvedLoopLimits(
            effectiveDeadline,
            limitKind,
            limitKind == LoopLimitKind.TimeBudget ? configured.TimeBudget : null);
    }

    private Task EnsureTimeLimitNotExceededAsync(
        DurableLoopScope scope,
        ResolvedLoopLimits limits,
        string? dependencyStepKey,
        CancellationToken cancellationToken) =>
        EnsureTimeLimitNotExceededAsync(
            scope,
            limits,
            dependencyStepKey is null ? [] : [dependencyStepKey],
            cancellationToken);

    private async Task EnsureTimeLimitNotExceededAsync(
        DurableLoopScope scope,
        ResolvedLoopLimits limits,
        IReadOnlyCollection<string> dependencyStepKeys,
        CancellationToken cancellationToken)
    {
        // Rollback replay reconstructs already committed forward work only so
        // compensation delegates can be rebound. A wall-clock limit that
        // expires afterward must not prevent recovery of that prior work.
        if (IsRollback)
            return;

        if (limits.EffectiveDeadline is not { } deadline ||
            timeProvider.GetUtcNow() <= deadline)
        {
            return;
        }

        await FailLoopLimitAsync(
            scope,
            new LoopLimitExceededEvent(
                scope.DisplayPath,
                limits.LimitKind!.Value,
                null,
                deadline,
                limits.TimeBudget),
            dependencyStepKeys,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task FailLoopLimitAsync(
        DurableLoopScope scope,
        LoopLimitExceededEvent evidence,
        IReadOnlyCollection<string> dependencyStepKeys,
        CancellationToken cancellationToken)
    {
        await StepAsync(
            DurableLoopStepKeys.Limit(scope),
            evidence,
            async (value, step, token) =>
            {
                await step.EmitAsync(
                    WorkflowEventTypes.LoopLimitExceeded,
                    value,
                    token).ConfigureAwait(false);
                return value;
            },
            dependencyStepKeys.Count == 0
                ? null
                : new StepOptions { DependsOn = dependencyStepKeys },
            cancellationToken).ConfigureAwait(false);

        throw evidence.LimitKind == LoopLimitKind.IterationCount
            ? new LoopLimitExceededException(
                evidence.LoopPath,
                evidence.MaxIterations!.Value)
            : new LoopLimitExceededException(
                evidence.LoopPath,
                evidence.LimitKind,
                evidence.Deadline!.Value,
                evidence.TimeBudget);
    }

    private Task<TState> CompleteLoopAsync<TState>(
        DurableLoopScope scope,
        TState state,
        int iterations,
        LoopCompletionReason reason,
        string dependencyStepKey,
        CancellationToken cancellationToken) =>
        StepAsync(
            scope.FinalStepKey,
            state,
            async (value, step, token) =>
            {
                await step.EmitAsync(
                    WorkflowEventTypes.LoopCompleted,
                    new LoopCompletedEvent(
                        scope.DisplayPath,
                        iterations,
                        reason),
                    token).ConfigureAwait(false);
                return value;
            },
            new StepOptions { DependsOn = [dependencyStepKey] },
            cancellationToken);

    private sealed record LoopCommittedOutcome<TState>(
        LoopBodyOutcomeKind Kind,
        TState State);

    private sealed record LoopIterationCommittedEvent(
        string LoopPath,
        int Iteration,
        LoopBodyOutcomeKind Kind);

    private sealed record LoopCompletedEvent(
        string LoopPath,
        int Iterations,
        LoopCompletionReason Reason);

    private sealed record ConfiguredLoopLimits(
        int MaxIterations,
        DateTimeOffset? Deadline,
        TimeSpan? TimeBudget);

    private sealed record LoopLimitResolution(
        ConfiguredLoopLimits Configuration,
        DateTimeOffset BudgetStartedAt);

    private sealed record ResolvedLoopLimits(
        DateTimeOffset? EffectiveDeadline,
        LoopLimitKind? LimitKind,
        TimeSpan? TimeBudget)
    {
        public static ResolvedLoopLimits None { get; } = new(null, null, null);
    }

    private sealed record LoopLimitExceededEvent(
        string LoopPath,
        LoopLimitKind LimitKind,
        int? MaxIterations,
        DateTimeOffset? Deadline,
        TimeSpan? TimeBudget);

    private sealed record LoopConditionInput<TState>(TState State, int MaxIterations);

    private enum LoopCompletionReason
    {
        ConditionFalse,
        Break
    }
}
