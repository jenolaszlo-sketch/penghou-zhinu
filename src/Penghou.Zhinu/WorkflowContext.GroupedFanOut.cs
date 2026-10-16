using System.Globalization;
using System.Text.Json;

namespace Penghou.Zhinu;

public sealed partial class WorkflowContext
{
    private int usedGroupFanOut;
    internal bool HasUsedGroupFanOut => Volatile.Read(ref usedGroupFanOut) != 0;

    private async Task<IReadOnlyList<TOutput>> RunGroupedFanOutAsync<TInput, TOutput>(
        string fanOutKey, IReadOnlyList<TInput> inputs, Func<TInput, string>? keySelector,
        Func<string, TInput, StepOptions, CancellationToken, Task<TOutput>> runItem,
        StepOptions configured, int? maxDegree, string? implementationKey,
        StepCompensationMode compensation, CancellationToken cancellationToken)
    {
        if (inputs.Count > ActivityGroupFanOutPlan.MaximumAllowedItems)
            throw new WorkflowConfigurationException("Grouped fan-out exceeds its item limit.");
        var snapshot = new TInput[inputs.Count];
        for (var index = 0; index < snapshot.Length; index++) snapshot[index] = inputs[index];
        var kind = keySelector is null ? ActivityGroupFanOutIdentityKind.Positional : ActivityGroupFanOutIdentityKind.Keyed;
        var keys = keySelector is null
            ? Enumerable.Range(0, snapshot.Length).Select(index => index.ToString(CultureInfo.InvariantCulture)).ToArray()
            : ValidateItemKeys(fanOutKey, snapshot, keySelector);
        var hashes = snapshot.Select(input => SerializationIdentity.Hash(JsonSerializer.Serialize(input, serializerOptions))).ToArray();
        var plan = ActivityGroupFanOutPlan.Create(WorkflowRunId, groupManifest!, fanOutKey, configured.FanOutGroup!, kind,
            SerializationIdentity.TypeId(typeof(TInput)), SerializationIdentity.TypeId(typeof(TOutput)), implementationKey,
            options.ExecutionAuthorization is null ? null :
                WorkflowAuthorizationCodec.Declaration(configured.Authorization, configured.CompensationAuthorization),
            ActivityGroupFanOutPlan.CompensationSignature(compensation == StepCompensationMode.None ? null :
                new CompensationMetadata(string.Empty, JsonSerializer.Serialize(configured.Retry, serializerOptions), configured.ExecutionTimeout)
                {
                    AuthorizationDeclarationJson = options.ExecutionAuthorization is null ? null :
                        WorkflowAuthorizationCodec.SerializeDeclaration(configured.CompensationAuthorization)
                }),
            ResolveDependencies(configured) ?? [], keys.Select((key, index) => (key, hashes[index])));
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
                .ConfigureAwait(false) ?? throw new WorkflowConfigurationException("Rollback has no admitted grouped fan-out to replay.");
            if (admitted.PlanDigest != plan.PlanDigest)
                throw new WorkflowConfigurationException("Rollback grouped fan-out does not match its immutable enumeration.");
        }
        else
        {
            admitted = await repository.AdmitGroupFanOutAsync(request, linkedCancellation.Token).ConfigureAwait(false);
            Volatile.Write(ref usedGroupFanOut, 1);
        }
        var items = plan.Items.ToDictionary(item => item.ItemKey, StringComparer.Ordinal);
        var results = await FanOutChunkedAsync(keys, snapshot, (key, input, ct) =>
        {
            var item = items[key];
            var itemOptions = configured with
            {
                FanOutGroup = null,
                Group = item.Group,
                GroupFanOutExecutionScope = item.Group.Occurrence.ExecutionCoordinate,
                FanOutClaim = new ActivityGroupFanOutItemClaim { ExpansionId = admitted.ExpansionId, ItemKey = key },
                DependsOn = plan.Dependencies
            };
            return runItem(item.StepKey, input, itemOptions, ct);
        }, maxDegree ?? Math.Max(1, keys.Count), linkedCancellation.Token).ConfigureAwait(false);
        if (!IsRollback)
            await repository.CompleteGroupFanOutAsync(request with { Now = timeProvider.GetUtcNow() }, linkedCancellation.Token)
                .ConfigureAwait(false);
        return results;
    }
}
