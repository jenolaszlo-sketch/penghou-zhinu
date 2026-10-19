using System.Text.Json;

namespace Penghou.Zhinu;

public sealed partial class WorkflowContext
{
    private readonly AsyncLocal<int> groupedFanOutBodyDepth = new();
    internal bool IsInGroupedFanOutBody => groupedFanOutBodyDepth.Value != 0;
    /// <summary>
    /// Executes a bounded keyed fan-out body whose operations are independently durable steps.
    /// Each item completion runs only after its body has returned and every registered child step has settled.
    /// </summary>
    public Task<IReadOnlyList<TOutput>> FanOutBodyAsync<TInput, TOutput>(
        string stepKeyPrefix,
        IReadOnlyList<TInput> inputs,
        Func<TInput, string> keySelector,
        Func<TInput, ActivityGroupFanOutItemScope, CancellationToken, Task<TOutput>> body,
        string bodyContract,
        FanOutOptions options,
        StepOptions stepOptions,
        CancellationToken cancellationToken = default)
    {
        ValidateStepKey(stepKeyPrefix);
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(keySelector);
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(stepOptions);
        options.Validate();
        RejectGroupedFanOut(stepOptions);
        ActivityGroupText.Required(bodyContract, 512, nameof(bodyContract));
        if (stepOptions.FanOutGroup is null || stepOptions.FanOutGroup.Membership.Role != ActivityGroupMemberRole.Control)
            throw new WorkflowConfigurationException("Grouped fan-out bodies require a Control item-completion binding.");
        if (store is not IWorkflowGroupFanOutBodyRepository)
            throw new WorkflowConfigurationException("Grouped fan-out bodies require the durable body-claim repository capability.");
        return RunGroupedFanOutBodyAsync(stepKeyPrefix, inputs, keySelector, body, bodyContract,
            stepOptions, options.MaxDegreeOfParallelism, cancellationToken);
    }

    private async Task<IReadOnlyList<TOutput>> RunGroupedFanOutBodyAsync<TInput, TOutput>(
        string fanOutKey,
        IReadOnlyList<TInput> inputs,
        Func<TInput, string> keySelector,
        Func<TInput, ActivityGroupFanOutItemScope, CancellationToken, Task<TOutput>> body,
        string bodyContract,
        StepOptions configured,
        int maxDegree,
        CancellationToken cancellationToken)
    {
        if (inputs.Count > ActivityGroupFanOutPlan.MaximumAllowedItems)
            throw new WorkflowConfigurationException("Grouped fan-out exceeds its item limit.");
        var snapshot = new TInput[inputs.Count];
        for (var index = 0; index < snapshot.Length; index++) snapshot[index] = inputs[index];
        var keys = ValidateItemKeys(fanOutKey, snapshot, keySelector);
        var hashes = snapshot.Select(input => SerializationIdentity.Hash(JsonSerializer.Serialize(input, serializerOptions))).ToArray();
        var plan = ActivityGroupFanOutPlan.Create(WorkflowRunId, groupManifest!, fanOutKey, configured.FanOutGroup!,
            ActivityGroupFanOutIdentityKind.Keyed,
            SerializationIdentity.TypeId(typeof(TInput)), SerializationIdentity.TypeId(typeof(TOutput)), null,
            configured.Authorization is null ? null :
                WorkflowAuthorizationCodec.Declaration(configured.Authorization, configured.CompensationAuthorization),
            ActivityGroupFanOutPlan.CompensationSignature(null),
            ResolveDependencies(configured) ?? [], keys.Select((key, index) => (key, hashes[index])), bodyContract);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(workflowCancellationToken, cancellationToken);
        var repository = (IWorkflowGroupFanOutRepository)store;
        var request = new ActivityGroupFanOutRequest
        {
            Plan = plan,
            OwnerId = ownerId,
            LeaseGeneration = leaseGeneration,
            Now = timeProvider.GetUtcNow()
        };
        ActivityGroupFanOutState admitted;
        if (IsRollback)
        {
            admitted = await repository.GetGroupFanOutAsync(WorkflowRunId, fanOutKey, linkedCancellation.Token)
                .ConfigureAwait(false) ?? throw new WorkflowConfigurationException("Rollback has no admitted grouped fan-out body to replay.");
            if (admitted.PlanDigest != plan.PlanDigest)
                throw new WorkflowConfigurationException("Rollback grouped fan-out body does not match its immutable contract.");
        }
        else
        {
            admitted = await repository.AdmitGroupFanOutAsync(request, linkedCancellation.Token).ConfigureAwait(false);
            Volatile.Write(ref usedGroupFanOut, 1);
        }

        var items = plan.Items.ToDictionary(item => item.ItemKey, StringComparer.Ordinal);
        var reservedItemKeys = plan.Items.Select(item => item.StepKey).ToHashSet(StringComparer.Ordinal);
        var operationBudget = new ActivityGroupFanOutBodyBudget();
        var outputs = await FanOutChunkedAsync(keys, snapshot, async (key, input, ct) =>
        {
            var item = items[key];
            var itemClaim = new ActivityGroupFanOutItemClaim
            {
                ExpansionId = admitted.ExpansionId,
                ItemKey = key
            };
            var scope = new ActivityGroupFanOutItemScope(this, item.Group, itemClaim, fanOutKey,
                reservedItemKeys, plan.Dependencies, operationBudget, item.Group.Occurrence.ExecutionCoordinate);
            TOutput result;
            var previousBodyDepth = groupedFanOutBodyDepth.Value;
            groupedFanOutBodyDepth.Value = previousBodyDepth + 1;
            try
            {
                result = await body(input, scope, ct).ConfigureAwait(false);
            }
            catch
            {
                try { await scope.SealAndWaitAsync(requireOperation: false).ConfigureAwait(false); }
                catch { }
                throw;
            }
            finally
            {
                groupedFanOutBodyDepth.Value = previousBodyDepth;
            }
            var childKeys = await scope.SealAndWaitAsync().ConfigureAwait(false);
            var dependencies = new HashSet<string>(plan.Dependencies, StringComparer.Ordinal);
            dependencies.UnionWith(childKeys);
            var completionOptions = configured with
            {
                FanOutGroup = null,
                Group = new ActivityGroupStepBinding(
                    new ActivityGroupMembership(item.Group.Membership.GroupKey, ActivityGroupMemberRole.Control),
                    item.Group.Occurrence),
                GroupFanOutExecutionScope = item.Group.Occurrence.ExecutionCoordinate,
                FanOutClaim = itemClaim with { IsBodyCompletion = true },
                DependsOn = dependencies
            };
            return await StepAsync(item.StepKey, input, (_, _) => Task.FromResult(result), completionOptions, ct)
                .ConfigureAwait(false);
        }, maxDegree, linkedCancellation.Token).ConfigureAwait(false);

        if (!IsRollback)
            await repository.CompleteGroupFanOutAsync(request with { Now = timeProvider.GetUtcNow() }, linkedCancellation.Token)
                .ConfigureAwait(false);
        return outputs;
    }

    internal ActivityGroupOccurrence CreateFanOutBodyGroup(
        string groupKey, ActivityGroupOccurrence parent, ActivityGroupOccurrence itemRoot, string coordinate)
    {
        if (!IsFanOutDescendant(parent, itemRoot))
            throw new WorkflowConfigurationException("A fan-out body group must descend from its own selected item occurrence.");
        return ActivityGroupOccurrence.Create(WorkflowRunId, groupManifest!, groupKey, parent, coordinate);
    }

    internal void ValidateFanOutBodyGroup(
        ActivityGroupStepBinding binding, ActivityGroupOccurrence itemRoot, string coordinate)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (!IsFanOutDescendant(binding.Occurrence, itemRoot) || binding.Occurrence.ExecutionCoordinate != coordinate)
            throw new WorkflowConfigurationException("A fan-out body operation must use its item's occurrence or a static descendant at the same coordinate.");
        if (binding.Membership.Role != ActivityGroupMemberRole.Work)
            throw new WorkflowConfigurationException("Fan-out body operations must use Work membership.");
        ActivityGroupRuntime.ValidateBinding(WorkflowRunId, groupManifest, binding, fanOutCoordinate: coordinate);
    }

    internal void ValidateFanOutBodyStepKey(string stepKey) => ValidateStepKey(stepKey);

    internal string CreateFanOutBodyStepKey(string fanOutKey, string itemKey, string operationKey) =>
        fanOutKey + ":body:" + EncodeFanOutBodySegment(itemKey) + ":" + EncodeFanOutBodySegment(operationKey);

    private static string EncodeFanOutBodySegment(string value) =>
        Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    internal Task<TOutput> ExecuteFanOutBodyStepAsync<TInput, TOutput>(
        string stepKey, TInput input,
        Func<TInput, WorkflowStepContext, CancellationToken, Task<TOutput>> operation,
        StepOptions? options, ActivityGroupStepBinding group, ActivityGroupFanOutItemClaim claim,
        string coordinate, IReadOnlyCollection<string> requiredDependencies, CancellationToken cancellationToken) =>
        StepAsync(stepKey, input, operation, BodyStepOptions(options, group, claim, coordinate, requiredDependencies), cancellationToken);

    internal Task<TOutput> ExecuteFanOutBodyStepAsync<TInput, TOutput>(
        string stepKey, WorkflowStepReference<TInput, TOutput> step, TInput input,
        StepOptions? options, ActivityGroupStepBinding group, ActivityGroupFanOutItemClaim claim,
        string coordinate, IReadOnlyCollection<string> requiredDependencies, CancellationToken cancellationToken, StepCompensationMode compensation) =>
        StepAsync(stepKey, step, input, BodyStepOptions(options, group, claim, coordinate, requiredDependencies), cancellationToken, compensation);

    private StepOptions BodyStepOptions(StepOptions? options, ActivityGroupStepBinding group,
        ActivityGroupFanOutItemClaim claim, string coordinate, IReadOnlyCollection<string> requiredDependencies)
    {
        if (options?.FanOutGroup is not null)
            throw new WorkflowConfigurationException("Dynamic grouped fan-out cannot be nested inside a fan-out body.");
        var configured = options ?? new StepOptions();
        var dependencies = ResolveDependencies(configured)?.ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>(StringComparer.Ordinal);
        dependencies.UnionWith(requiredDependencies);
        return configured with
        {
            Group = group,
            GroupExecutionScope = null,
            GroupFanOutExecutionScope = coordinate,
            FanOutClaim = claim,
            DependsOn = dependencies
        };
    }

    private static bool IsFanOutDescendant(ActivityGroupOccurrence occurrence, ActivityGroupOccurrence root)
    {
        for (var current = occurrence; current is not null; current = current.Parent!)
            if (current.GroupOccurrenceId == root.GroupOccurrenceId) return true;
        return false;
    }
}
