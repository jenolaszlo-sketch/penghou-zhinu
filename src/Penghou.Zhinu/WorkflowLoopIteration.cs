using System.Collections.Concurrent;

namespace Penghou.Zhinu;

/// <summary>
/// Executes durable body steps for one logical iteration of a state loop.
/// Body work should use this surface so Zhinu can record iteration ownership
/// and restart dependencies.
/// </summary>
public sealed class WorkflowLoopIteration<TState>
{
    private readonly WorkflowContext workflow;
    private readonly DurableLoopIterationIdentity identity;
    private readonly string conditionStepKey;
    private readonly object controlScope = new();
    private readonly ConcurrentDictionary<string, byte> bodyStepKeys =
        new(StringComparer.Ordinal);

    internal WorkflowLoopIteration(
        WorkflowContext workflow,
        DurableLoopIterationIdentity identity,
        TState state,
        string conditionStepKey)
    {
        this.workflow = workflow;
        this.identity = identity;
        this.conditionStepKey = conditionStepKey;
        State = state;
    }

    public string LoopKey => identity.Scope.Name;

    /// <summary>The one-based logical iteration number.</summary>
    public int Iteration => identity.Number;

    /// <summary>The immutable state committed by the previous iteration.</summary>
    public TState State { get; }

    /// <summary>
    /// Commits <paramref name="nextState"/> and requests evaluation of the
    /// next iteration's continuation condition.
    /// </summary>
    public LoopBodyOutcome<TState> Continue(TState nextState) =>
        new(controlScope, LoopBodyOutcomeKind.Continue, nextState);

    /// <summary>
    /// Commits <paramref name="finalState"/> and completes this loop normally
    /// without evaluating another continuation condition.
    /// </summary>
    public LoopBodyOutcome<TState> Break(TState finalState) =>
        new(controlScope, LoopBodyOutcomeKind.Break, finalState);

    /// <summary>
    /// Executes a durable nested loop owned by this outer iteration. Nested
    /// identity includes the parent loop instance and iteration, so repeated
    /// inner names cannot collide across outer iterations.
    /// </summary>
    public Task<TNestedState> LoopAsync<TNestedState>(
        string loopKey,
        TNestedState initialState,
        Func<TNestedState, bool> continueWhile,
        Func<WorkflowLoopIteration<TNestedState>, CancellationToken, Task<LoopBodyOutcome<TNestedState>>> body,
        LoopOptions options,
        CancellationToken cancellationToken = default)
    {
        var nestedScope = identity.Scope.Nest(identity, loopKey);
        bodyStepKeys.TryAdd(nestedScope.FinalStepKey, 0);
        return workflow.LoopCoreAsync(
            nestedScope,
            initialState,
            continueWhile,
            body,
            options,
            conditionStepKey,
            cancellationToken);
    }

    /// <summary>
    /// Declares iteration-local dependencies for steps created until the
    /// returned scope is disposed. Names are resolved within this iteration.
    /// </summary>
    public IDisposable DependsOn(params string[] stepNames)
    {
        ArgumentNullException.ThrowIfNull(stepNames);
        var stepKeys = new string[stepNames.Length];
        for (var index = 0; index < stepNames.Length; index++)
        {
            DurableLoopScope.ValidateName(stepNames[index], nameof(stepNames));
            stepKeys[index] = DurableLoopStepKeys.Body(
                identity,
                stepNames[index]);
        }
        return workflow.DependsOn(stepKeys);
    }

    /// <summary>Executes or reuses a durable step owned by this iteration.</summary>
    public Task<TOutput> StepAsync<TOutput>(
        string stepName,
        Func<WorkflowStepContext, CancellationToken, Task<TOutput>> operation,
        StepOptions? options = null,
        CancellationToken cancellationToken = default,
        Func<TOutput, WorkflowStepContext, CancellationToken, Task>? compensation = null)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var stepKey = RegisterStep(stepName);
        return workflow.StepAsync(
            stepKey,
            operation,
            WithConditionDependency(options),
            cancellationToken,
            compensation);
    }

    /// <summary>
    /// Executes or reuses a durable iteration step with an explicit input.
    /// </summary>
    public Task<TOutput> StepAsync<TInput, TOutput>(
        string stepName,
        TInput input,
        Func<TInput, WorkflowStepContext, CancellationToken, Task<TOutput>> operation,
        StepOptions? options = null,
        CancellationToken cancellationToken = default,
        Func<TOutput, WorkflowStepContext, CancellationToken, Task>? compensation = null)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var stepKey = RegisterStep(stepName);
        return workflow.StepAsync(
            stepKey,
            input,
            operation,
            WithConditionDependency(options),
            cancellationToken,
            compensation);
    }

    /// <summary>
    /// Executes or reuses a registered class-based step owned by this
    /// iteration.
    /// </summary>
    public Task<TOutput> StepAsync<TInput, TOutput>(
        string stepName,
        WorkflowStepReference<TInput, TOutput> step,
        TInput input,
        StepOptions? options = null,
        CancellationToken cancellationToken = default,
        StepCompensationMode compensation = StepCompensationMode.None)
    {
        ArgumentNullException.ThrowIfNull(step);
        var stepKey = RegisterStep(stepName);
        return workflow.StepAsync(
            stepKey,
            step,
            input,
            WithConditionDependency(options),
            cancellationToken,
            compensation);
    }

    internal IReadOnlyCollection<string> GetBodyStepKeys() =>
        bodyStepKeys.Keys.Order(StringComparer.Ordinal).ToArray();

    internal void ValidateOutcome(LoopBodyOutcome<TState>? outcome)
    {
        if (outcome is null)
        {
            throw new WorkflowStateException(
                $"Loop '{LoopKey}' iteration {Iteration} returned no control outcome.");
        }
        if (!outcome.BelongsTo(controlScope))
        {
            throw new WorkflowStateException(
                $"Loop '{LoopKey}' iteration {Iteration} returned an outcome created by a different loop scope.");
        }
    }

    private string RegisterStep(string stepName)
    {
        DurableLoopScope.ValidateName(stepName, nameof(stepName));
        var stepKey = DurableLoopStepKeys.Body(identity, stepName);
        bodyStepKeys.TryAdd(stepKey, 0);
        return stepKey;
    }

    private StepOptions WithConditionDependency(StepOptions? options)
    {
        var dependencies = new HashSet<string>(StringComparer.Ordinal)
        {
            conditionStepKey
        };
        if (options?.DependsOn is not null)
            dependencies.UnionWith(options.DependsOn);
        return new StepOptions
        {
            Retry = options?.Retry ?? new RetryPolicy(),
            ExecutionTimeout = options?.ExecutionTimeout,
            DependsOn = dependencies
        };
    }
}
