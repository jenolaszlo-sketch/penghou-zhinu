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
    public async Task<TState> LoopAsync<TState>(
        string loopKey,
        TState initialState,
        Func<TState, bool> continueWhile,
        Func<WorkflowLoopIteration<TState>, CancellationToken, Task<LoopBodyOutcome<TState>>> body,
        LoopOptions options,
        CancellationToken cancellationToken = default)
    {
        DurableLoopIdentity.ValidateName(loopKey, nameof(loopKey));
        ArgumentNullException.ThrowIfNull(continueWhile);
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(options);

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
        for (var iteration = 1; ; iteration++)
        {
            var conditionStepKey = DurableLoopIdentity.ConditionStep(loopKey, iteration);
            var conditionOptions = previousCommitStepKey is null
                ? null
                : new StepOptions { DependsOn = [previousCommitStepKey] };
            var shouldContinue = await StepAsync(
                conditionStepKey,
                new LoopConditionInput<TState>(state, options.MaxIterations),
                (value, _) => Task.FromResult(continueWhile(value.State)),
                conditionOptions,
                cancellationToken).ConfigureAwait(false);

            if (!shouldContinue)
            {
                return await CompleteLoopAsync(
                    loopKey,
                    state,
                    iteration - 1,
                    LoopCompletionReason.ConditionFalse,
                    conditionStepKey,
                    cancellationToken).ConfigureAwait(false);
            }

            if (iteration > options.MaxIterations)
            {
                var limitStepKey = DurableLoopIdentity.LimitStep(loopKey);
                await StepAsync(
                    limitStepKey,
                    options.MaxIterations,
                    async (limit, step, token) =>
                    {
                        await step.EmitAsync(
                            WorkflowEventTypes.LoopLimitExceeded,
                            new LoopLimitExceededEvent(loopKey, limit),
                            token).ConfigureAwait(false);
                        return limit;
                    },
                    new StepOptions { DependsOn = [conditionStepKey] },
                    cancellationToken).ConfigureAwait(false);
                throw new LoopLimitExceededException(loopKey, options.MaxIterations);
            }

            var commitStepKey = DurableLoopIdentity.CommitStep(loopKey, iteration);
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
                        loopKey,
                        state,
                        iteration,
                        LoopCompletionReason.Break,
                        commitStepKey,
                        cancellationToken).ConfigureAwait(false);
                }
                continue;
            }

            var loopIteration = new WorkflowLoopIteration<TState>(
                this,
                loopKey,
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
                            loopKey,
                            iteration,
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
                    loopKey,
                    state,
                    iteration,
                    LoopCompletionReason.Break,
                    commitStepKey,
                    cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private Task<TState> CompleteLoopAsync<TState>(
        string loopKey,
        TState state,
        int iterations,
        LoopCompletionReason reason,
        string dependencyStepKey,
        CancellationToken cancellationToken) =>
        StepAsync(
            loopKey,
            state,
            async (value, step, token) =>
            {
                await step.EmitAsync(
                    WorkflowEventTypes.LoopCompleted,
                    new LoopCompletedEvent(loopKey, iterations, reason),
                    token).ConfigureAwait(false);
                return value;
            },
            new StepOptions { DependsOn = [dependencyStepKey] },
            cancellationToken);

    private sealed record LoopCommittedOutcome<TState>(
        LoopBodyOutcomeKind Kind,
        TState State);

    private sealed record LoopIterationCommittedEvent(
        string LoopKey,
        int Iteration,
        LoopBodyOutcomeKind Kind);

    private sealed record LoopCompletedEvent(
        string LoopKey,
        int Iterations,
        LoopCompletionReason Reason);

    private sealed record LoopLimitExceededEvent(string LoopKey, int MaxIterations);

    private sealed record LoopConditionInput<TState>(TState State, int MaxIterations);

    private enum LoopCompletionReason
    {
        ConditionFalse,
        Break
    }
}

internal static class DurableLoopIdentity
{
    private const int MaximumNameLength = 128;

    public static string ConditionStep(string loopKey, int iteration) =>
        $"$loop/{loopKey}/{iteration}/condition";

    public static string BodyStep(string loopKey, int iteration, string stepName) =>
        $"$loop/{loopKey}/{iteration}/body/{stepName}";

    public static string CommitStep(string loopKey, int iteration) =>
        $"$loop/{loopKey}/{iteration}/commit";

    public static string LimitStep(string loopKey) => $"$loop/{loopKey}/limit";

    public static void ValidateName(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > MaximumNameLength)
        {
            throw new ArgumentException(
                $"Durable loop names cannot exceed {MaximumNameLength} characters.",
                parameterName);
        }
        foreach (var character in value)
        {
            if (char.IsAsciiLetterOrDigit(character) ||
                character is '_' or '-' or '.')
            {
                continue;
            }
            throw new ArgumentException(
                "Durable loop names may contain only ASCII letters, digits, '_', '-', and '.'.",
                parameterName);
        }
    }
}
