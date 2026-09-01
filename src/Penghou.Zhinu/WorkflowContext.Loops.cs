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

        IReadOnlyDictionary<string, WorkflowStepRun> completedSteps = IsRollback
            ? new Dictionary<string, WorkflowStepRun>(StringComparer.Ordinal)
            : (await store.GetStepsAsync(
                    WorkflowRunId,
                    cancellationToken)
                .ConfigureAwait(false))
                .Where(step => step.Status == StepStatus.Completed)
                .ToDictionary(step => step.StepKey, StringComparer.Ordinal);
        var state = initialState;
        string? previousCommitStepKey = null;
        for (var iterationNumber = 1; ; iterationNumber++)
        {
            var iteration = scope.Iteration(iterationNumber);
            var conditionStepKey = DurableLoopStepKeys.Condition(iteration);
            var conditionDependency = previousCommitStepKey ?? entryDependencyStepKey;
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
                var limitStepKey = DurableLoopStepKeys.Limit(scope);
                await StepAsync(
                    limitStepKey,
                    loopOptions.MaxIterations,
                    async (limit, step, token) =>
                    {
                        await step.EmitAsync(
                            WorkflowEventTypes.LoopLimitExceeded,
                            new LoopLimitExceededEvent(scope.DisplayPath, limit),
                            token).ConfigureAwait(false);
                        return limit;
                    },
                    new StepOptions { DependsOn = [conditionStepKey] },
                    cancellationToken).ConfigureAwait(false);
                throw new LoopLimitExceededException(
                    scope.DisplayPath,
                    loopOptions.MaxIterations);
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

    private sealed record LoopLimitExceededEvent(
        string LoopPath,
        int MaxIterations);

    private sealed record LoopConditionInput<TState>(TState State, int MaxIterations);

    private enum LoopCompletionReason
    {
        ConditionFalse,
        Break
    }
}
