namespace Penghou.Zhinu;

internal sealed class ActivityGroupFanOutBodyBudget
{
    private int registered;
    internal const int MaximumOperationsPerExpansion = 65_536;

    internal bool TryRegister() => Interlocked.Increment(ref registered) <= MaximumOperationsPerExpansion;
}

/// <summary>Creates independently durable operations within one admitted keyed fan-out item.</summary>
public sealed class ActivityGroupFanOutItemScope
{
    private readonly WorkflowContext workflow;
    private readonly ActivityGroupStepBinding rootGroup;
    private readonly ActivityGroupFanOutItemClaim claim;
    private readonly string fanOutKey;
    private readonly IReadOnlySet<string> reservedItemKeys;
    private readonly IReadOnlyCollection<string> requiredDependencies;
    private readonly ActivityGroupFanOutBodyBudget budget;
    private readonly string coordinate;
    private readonly object sync = new();
    private readonly HashSet<string> operationKeys = new(StringComparer.Ordinal);
    private readonly List<Task> pending = [];
    private bool sealedScope;

    internal ActivityGroupFanOutItemScope(WorkflowContext workflow, ActivityGroupStepBinding rootGroup,
        ActivityGroupFanOutItemClaim claim, string fanOutKey,
        IReadOnlySet<string> reservedItemKeys, IReadOnlyCollection<string> requiredDependencies,
        ActivityGroupFanOutBodyBudget budget, string coordinate)
    {
        this.workflow = workflow;
        this.rootGroup = rootGroup;
        this.claim = claim;
        this.fanOutKey = fanOutKey;
        this.reservedItemKeys = reservedItemKeys;
        this.requiredDependencies = requiredDependencies;
        this.budget = budget;
        this.coordinate = coordinate;
        Group = new ActivityGroupStepBinding(
            new ActivityGroupMembership(rootGroup.Membership.GroupKey, ActivityGroupMemberRole.Work),
            rootGroup.Occurrence);
    }

    /// <summary>The default Work membership for every operation in this item.</summary>
    public ActivityGroupStepBinding Group { get; }

    /// <summary>Returns the deterministic durable key for an operation in this item.</summary>
    public string GetStepKey(string operationKey)
    {
        ActivityGroupText.Required(operationKey, 512, nameof(operationKey));
        var stepKey = workflow.CreateFanOutBodyStepKey(fanOutKey, claim.ItemKey, operationKey);
        workflow.ValidateFanOutBodyStepKey(stepKey);
        if (reservedItemKeys.Contains(stepKey))
            throw new WorkflowConfigurationException("A fan-out body operation key collides with a reserved item completion key.");
        return stepKey;
    }

    /// <summary>Creates a static descendant occurrence at this item's existing keyed coordinate.</summary>
    public ActivityGroupOccurrence GetActivityGroup(string groupKey, ActivityGroupOccurrence? parent = null)
    {
        lock (sync)
        {
            if (sealedScope) throw new WorkflowStateException("The fan-out item scope is already complete.");
        }
        return workflow.CreateFanOutBodyGroup(groupKey, parent ?? rootGroup.Occurrence, rootGroup.Occurrence, coordinate);
    }

    /// <summary>Executes one independently durable functional step in this item.</summary>
    public Task<TOutput> StepAsync<TInput, TOutput>(
        string operationKey,
        TInput input,
        Func<TInput, WorkflowStepContext, CancellationToken, Task<TOutput>> operation,
        StepOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var (stepKey, binding, tracked) = Register(operationKey, options?.Group);
        Task<TOutput> task;
        try
        {
            task = workflow.ExecuteFanOutBodyStepAsync(
                stepKey, input, operation, options, binding, claim with { BodyOperationKey = stepKey }, coordinate,
                requiredDependencies, cancellationToken);
        }
        catch (Exception error)
        {
            tracked.TrySetException(error);
            throw;
        }
        Track(task, tracked);
        return task;
    }

    /// <summary>Executes one independently durable class-based step in this item.</summary>
    public Task<TOutput> StepAsync<TInput, TOutput>(
        string operationKey,
        WorkflowStepReference<TInput, TOutput> step,
        TInput input,
        StepOptions? options = null,
        CancellationToken cancellationToken = default,
        StepCompensationMode compensation = StepCompensationMode.None)
    {
        ArgumentNullException.ThrowIfNull(step);
        var (stepKey, binding, tracked) = Register(operationKey, options?.Group);
        Task<TOutput> task;
        try
        {
            task = workflow.ExecuteFanOutBodyStepAsync(
                stepKey, step, input, options, binding, claim with { BodyOperationKey = stepKey }, coordinate,
                requiredDependencies, cancellationToken, compensation);
        }
        catch (Exception error)
        {
            tracked.TrySetException(error);
            throw;
        }
        Track(task, tracked);
        return task;
    }

    internal async Task<IReadOnlyList<string>> SealAndWaitAsync(bool requireOperation = true)
    {
        Task[] tasks;
        string[] keys;
        lock (sync)
        {
            sealedScope = true;
            tasks = pending.ToArray();
            keys = operationKeys.Order(StringComparer.Ordinal).ToArray();
        }
        await Task.WhenAll(tasks).ConfigureAwait(false);
        if (requireOperation && keys.Length == 0)
            throw new WorkflowConfigurationException("A grouped fan-out body must claim at least one durable operation per item.");
        return keys;
    }

    private (string StepKey, ActivityGroupStepBinding Binding, TaskCompletionSource Tracked) Register(
        string operationKey, ActivityGroupStepBinding? requestedGroup)
    {
        ActivityGroupText.Required(operationKey, 512, nameof(operationKey));
        var stepKey = GetStepKey(operationKey);
        var binding = requestedGroup ?? Group;
        workflow.ValidateFanOutBodyGroup(binding, rootGroup.Occurrence, coordinate);
        if (binding.Membership.Role != ActivityGroupMemberRole.Work)
            throw new WorkflowConfigurationException("Fan-out body operations must use Work membership.");
        lock (sync)
        {
            if (sealedScope) throw new WorkflowStateException("The fan-out item scope is already complete.");
            if (!operationKeys.Add(stepKey))
                throw new WorkflowConfigurationException($"Fan-out body operation '{operationKey}' was registered more than once.");
            if (!budget.TryRegister())
            {
                operationKeys.Remove(stepKey);
                throw new WorkflowConfigurationException("The grouped fan-out body exceeds its operation limit.");
            }
            if (operationKeys.Count > ActivityGroupFanOutItemScopeLimits.MaximumOperationsPerItem)
                throw new WorkflowConfigurationException("The fan-out item body exceeds its operation limit.");
            var tracked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            pending.Add(tracked.Task);
            return (stepKey, binding, tracked);
        }
    }

    private static void Track(Task task, TaskCompletionSource tracked)
    {
        _ = ObserveAsync(task, tracked);
    }

    private static async Task ObserveAsync(Task task, TaskCompletionSource tracked)
    {
        try
        {
            await task.ConfigureAwait(false);
            tracked.TrySetResult();
        }
        catch (Exception error)
        {
            tracked.TrySetException(error);
        }
    }

    private static class ActivityGroupFanOutItemScopeLimits
    {
        internal const int MaximumOperationsPerItem = 4096;
    }
}
