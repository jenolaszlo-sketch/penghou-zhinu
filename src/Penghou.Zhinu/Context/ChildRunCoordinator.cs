using System.Text.Json;

namespace Penghou.Zhinu.Context;

/// <summary>
/// Creates child workflow runs and awaits their completion for the step
/// context. Child ids are derived deterministically from the parent run and
/// step key so replays reuse the same child.
/// </summary>
internal sealed class ChildRunCoordinator
{
    private readonly Guid workflowRunId;
    private readonly IWorkflowStore store;
    private readonly ZhinuOptions options;
    private readonly JsonSerializerOptions serializerOptions;
    private readonly TimeProvider timeProvider;
    private readonly string ownerId;
    private readonly long leaseGeneration;
    private readonly Func<Guid, CancellationToken, Task>? executeChildRun;
    private readonly IWorkflowRegistry? registry;

    public ChildRunCoordinator(
        Guid workflowRunId,
        IWorkflowStore store,
        ZhinuOptions options,
        JsonSerializerOptions serializerOptions,
        TimeProvider timeProvider,
        string ownerId,
        long leaseGeneration,
        Func<Guid, CancellationToken, Task>? executeChildRun,
        IWorkflowRegistry? registry = null)
    {
        this.workflowRunId = workflowRunId;
        this.store = store;
        this.options = options;
        this.serializerOptions = serializerOptions;
        this.timeProvider = timeProvider;
        this.ownerId = ownerId;
        this.leaseGeneration = leaseGeneration;
        this.executeChildRun = executeChildRun;
        this.registry = registry;
    }

    public async Task<Guid> CreateChildRunAsync(
        Guid parentRunId,
        string stepKey,
        int invocationGeneration,
        ChildStartRequest request,
        CancellationToken cancellationToken)
    {
        var childId = SerializationIdentity.HashId(
            $"{parentRunId:D}:{stepKey}:{invocationGeneration}");
        var parent = await store.GetRunAsync(parentRunId, cancellationToken)
            .ConfigureAwait(false) ??
            throw new WorkflowStateException(
                $"Parent workflow '{parentRunId:D}' does not exist.");
        WorkflowAuthorizationGate.ValidateBinding(parent, options);
        var childDepth = await GetRunDepthAsync(parentRunId, cancellationToken)
            .ConfigureAwait(false) + 1;
        if (childDepth > options.MaxNestingDepth)
        {
            throw new WorkflowStateException(
                $"Child workflow step '{stepKey}' exceeds the maximum nesting depth of {options.MaxNestingDepth}.");
        }
        var existing = await store.GetRunAsync(
            childId,
            cancellationToken).ConfigureAwait(false);
        var registration = ResolveRegistration(request);
        ValidateContract(request, registration, stepKey);
        if (existing is not null)
        {
            WorkflowAuthorizationGate.ValidateBinding(existing, options);
            if (!string.Equals(existing.WorkflowName, request.WorkflowName, StringComparison.Ordinal) ||
                !string.Equals(existing.WorkflowVersion, request.WorkflowVersion, StringComparison.Ordinal) ||
                !string.Equals(existing.InputJson, request.InputJson, StringComparison.Ordinal) ||
                !string.Equals(existing.InputType, request.InputType, StringComparison.Ordinal) ||
                !string.Equals(existing.OutputType, request.OutputType, StringComparison.Ordinal) ||
                existing.ParentRunId != parentRunId)
            {
                throw new WorkflowStateException(
                    $"Child workflow step '{stepKey}' is already associated with a different workflow or input.");
            }
            if (existing.DefinitionFingerprint is not null &&
                !string.Equals(
                    existing.DefinitionFingerprint,
                    registration.DefinitionFingerprint,
                    StringComparison.Ordinal))
            {
                throw new WorkflowSerializationException(
                    $"Registered definition for child workflow '{request.WorkflowName}' version " +
                    $"'{request.WorkflowVersion}' fingerprint does not match the admitted run's " +
                    $"recorded fingerprint '{existing.DefinitionFingerprint}'.");
            }
            // Legacy runs admitted without a fingerprint cannot prove their
            // definition; they replay as-is and are not restamped.
            await InitialGenerationBinding.EnsureAsync(
                store,
                childId,
                registration.DefinitionFingerprint,
                request.WorkflowName,
                request.WorkflowVersion,
                cancellationToken).ConfigureAwait(false);
            return childId;
        }
        var now = timeProvider.GetUtcNow();
        var deadline = EffectiveDeadline(parent.Deadline, request.Deadline);
        var metadataJson = ResolveMetadata(parent, request);
        await store.CreateRunAsync(
            new WorkflowRun
            {
                Id = childId,
                WorkflowName = request.WorkflowName,
                WorkflowVersion = request.WorkflowVersion,
                Status = WorkflowStatus.Pending,
                CreatedAt = now,
                UpdatedAt = now,
                InputJson = request.InputJson,
                InputType = request.InputType,
                OutputType = request.OutputType,
                ParentRunId = parentRunId,
                AuthorizationProviderId = options.ExecutionAuthorization?.ProviderId,
                AuthorizationBindingId = options.ExecutionAuthorization is { } authority
                    ? WorkflowAuthorizationCodec.Binding(authority) : null,
                Deadline = deadline,
                MetadataJson = metadataJson,
                DefinitionFingerprint = registration.DefinitionFingerprint,
                TraceId = parent.TraceId
            },
            cancellationToken).ConfigureAwait(false);
        await InitialGenerationBinding.BindAsync(
            store,
            childId,
            registration.DefinitionFingerprint,
            request.WorkflowName,
            request.WorkflowVersion,
            cancellationToken).ConfigureAwait(false);
        return childId;
    }

    private IWorkflowRegistration ResolveRegistration(ChildStartRequest request) =>
        registry?.TryGet(request.WorkflowName, request.WorkflowVersion, out var registration) == true
            ? registration!
            : throw new WorkflowDefinitionUnavailableException(
                request.WorkflowName,
                request.WorkflowVersion);

    private static void ValidateContract(
        ChildStartRequest request, IWorkflowRegistration registration, string stepKey)
    {
        if (!string.Equals(
                request.InputType,
                SerializationIdentity.TypeId(registration.InputType),
                StringComparison.Ordinal) ||
            !string.Equals(
                request.OutputType,
                SerializationIdentity.TypeId(registration.OutputType),
                StringComparison.Ordinal))
            throw new WorkflowSerializationException(
                $"Child workflow step '{stepKey}' contract does not match registered workflow " +
                $"'{request.WorkflowName}' version '{request.WorkflowVersion}'.");
    }

    private async Task<int> GetRunDepthAsync(
        Guid runId,
        CancellationToken cancellationToken)
    {
        var depth = 0;
        var current = await store.GetRunAsync(runId, cancellationToken)
            .ConfigureAwait(false);
        var visited = new HashSet<Guid>();
        while (current?.ParentRunId is { } parentId && visited.Add(parentId))
        {
            depth++;
            current = await store.GetRunAsync(parentId, cancellationToken)
                .ConfigureAwait(false);
            if (depth > options.MaxNestingDepth + 1)
                break;
        }
        return depth;
    }

    private static DateTimeOffset? EffectiveDeadline(
        DateTimeOffset? parentDeadline,
        DateTimeOffset? explicitDeadline)
    {
        if (parentDeadline is null)
            return explicitDeadline;
        if (explicitDeadline is null)
            return parentDeadline;
        return parentDeadline.Value < explicitDeadline.Value
            ? parentDeadline
            : explicitDeadline;
    }

    private string? ResolveMetadata(WorkflowRun parent, ChildStartRequest request)
    {
        if (request.MetadataJson is not null)
            return request.MetadataJson;
        if (request.InheritMetadata)
            return parent.MetadataJson;
        return null;
    }

    public async Task<TOutput> AwaitChildCoreAsync<TOutput>(
        Guid childId,
        string stepKey,
        int stepRevision,
        Guid stepId,
        CancellationToken cancellationToken)
    {
        var outputType = SerializationIdentity.TypeId(typeof(TOutput));
        var waits = store as IWorkflowWaitRepository;
        while (true)
        {
            var child = await GetChildAsync(childId, cancellationToken).ConfigureAwait(false);
            if (IsTerminal(child.Status))
            {
                await ConsumeChildWaitAsync(waits, stepKey, cancellationToken)
                    .ConfigureAwait(false);
                return ReadChildResult<TOutput>(child, outputType, childId, cancellationToken);
            }

            if (waits is not null)
            {
                var existing = await waits.GetWaitAsync(
                    workflowRunId, stepKey, cancellationToken).ConfigureAwait(false);
                if (existing is { Kind: WaitKind.Child })
                {
                    if (existing.Status == WaitStatus.Ready)
                    {
                        // The child reached a terminal state and flipped the
                        // wakeup; consume it and re-read the result.
                        await waits.CompleteWaitAsync(existing.WaitId, cancellationToken)
                            .ConfigureAwait(false);
                        continue;
                    }
                    // Still parked: keep this worker free until the child ends.
                    throw new ParkedExecutionException(existing.WaitId);
                }
            }

            // Drive the child inline when this worker can own its lease. This
            // occupies no extra capacity: the child runs synchronously here.
            if (executeChildRun is not null)
            {
                await executeChildRun(childId, cancellationToken).ConfigureAwait(false);
            }

            child = await GetChildAsync(childId, cancellationToken).ConfigureAwait(false);
            if (IsTerminal(child.Status))
                continue;

            // Another owner holds the child's lease: release this worker and
            // park until the child terminates and flips the wait ready.
            if (waits is not null &&
                child.LeaseOwner is not null &&
                !string.Equals(child.LeaseOwner, ownerId, StringComparison.Ordinal))
            {
                var parked = await waits.ParkWaitAsync(
                    new ParkWaitRequest
                    {
                        WorkflowRunId = workflowRunId,
                        StepKey = stepKey,
                        StepRevision = stepRevision,
                        StepId = stepId,
                        Kind = WaitKind.Child,
                        ChildRunId = childId,
                        LeaseGeneration = leaseGeneration,
                        Now = timeProvider.GetUtcNow()
                    },
                    cancellationToken).ConfigureAwait(false);
                throw new ParkedExecutionException(parked.WaitId);
            }

            await Task.Delay(
                options.PollInterval,
                timeProvider,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask<WorkflowRun> GetChildAsync(
        Guid childId,
        CancellationToken cancellationToken) =>
        await store.GetRunAsync(childId, cancellationToken).ConfigureAwait(false) ??
        throw new WorkflowStateException(
            $"Child workflow '{childId:D}' does not exist.");

    private async ValueTask ConsumeChildWaitAsync(
        IWorkflowWaitRepository? waits,
        string stepKey,
        CancellationToken cancellationToken)
    {
        if (waits is null)
            return;
        var wait = await waits.GetWaitAsync(workflowRunId, stepKey, cancellationToken)
            .ConfigureAwait(false);
        if (wait is { Kind: WaitKind.Child })
            await waits.CompleteWaitAsync(wait.WaitId, cancellationToken).ConfigureAwait(false);
    }

    private TOutput ReadChildResult<TOutput>(
        WorkflowRun child,
        string outputType,
        Guid childId,
        CancellationToken cancellationToken)
    {
        switch (child.Status)
        {
            case WorkflowStatus.Completed:
                if (!string.Equals(
                        child.OutputType,
                        outputType,
                        StringComparison.Ordinal))
                {
                    throw new WorkflowSerializationException(
                        $"Child workflow result was stored as '{child.OutputType}', not '{outputType}'.");
                }
                return StepResultSerializer.Deserialize<TOutput>(
                    child.OutputJson,
                    outputType,
                    serializerOptions);
            case WorkflowStatus.Failed:
                throw new WorkflowExecutionFailedException(
                    childId,
                    child.Error ?? new WorkflowError
                    {
                        Type = typeof(WorkflowStateException).FullName!,
                        Message = $"Child workflow '{childId:D}' failed without persisted details.",
                        Timestamp = timeProvider.GetUtcNow()
                    });
            case WorkflowStatus.Cancelled:
                throw new OperationCanceledException(
                    $"Child workflow '{childId:D}' was cancelled.",
                    cancellationToken);
            case WorkflowStatus.Compensated:
                throw new WorkflowStateException(
                    $"Child workflow '{childId:D}' was compensated and has no forward result to return.");
            default:
                throw new WorkflowStateException(
                    $"Child workflow '{childId:D}' is not terminal ({child.Status}).");
        }
    }

    private static bool IsTerminal(WorkflowStatus status) =>
        status is WorkflowStatus.Completed or WorkflowStatus.Failed
            or WorkflowStatus.Cancelled or WorkflowStatus.Compensated;

    internal sealed record ChildStartRequest(
        string WorkflowName,
        string WorkflowVersion,
        string InputJson,
        string InputType,
        string OutputType,
        DateTimeOffset? Deadline = null,
        string? MetadataJson = null,
        bool InheritMetadata = false);
}
