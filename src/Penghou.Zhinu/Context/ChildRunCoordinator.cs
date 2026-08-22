using System.Text.Json;

namespace Penghou.Zhinu.Context;

/// <summary>
/// Creates child workflow runs and awaits their completion for the step
/// context. Child ids are derived deterministically from the parent run and
/// step key so replays reuse the same child.
/// </summary>
internal sealed class ChildRunCoordinator
{
    private readonly IWorkflowStore store;
    private readonly ZhinuOptions options;
    private readonly JsonSerializerOptions serializerOptions;
    private readonly TimeProvider timeProvider;
    private readonly Func<Guid, CancellationToken, Task>? executeChildRun;

    public ChildRunCoordinator(
        IWorkflowStore store,
        ZhinuOptions options,
        JsonSerializerOptions serializerOptions,
        TimeProvider timeProvider,
        Func<Guid, CancellationToken, Task>? executeChildRun)
    {
        this.store = store;
        this.options = options;
        this.serializerOptions = serializerOptions;
        this.timeProvider = timeProvider;
        this.executeChildRun = executeChildRun;
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
        if (existing is not null)
        {
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
                Deadline = deadline,
                MetadataJson = metadataJson,
                TraceId = parent.TraceId
            },
            cancellationToken).ConfigureAwait(false);
        return childId;
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
        CancellationToken cancellationToken)
    {
        var outputType = SerializationIdentity.TypeId(typeof(TOutput));
        while (true)
        {
            var child = await store.GetRunAsync(childId, cancellationToken)
                .ConfigureAwait(false) ??
                throw new WorkflowStateException(
                    $"Child workflow '{childId:D}' does not exist.");
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
            }
            if (executeChildRun is not null)
            {
                await executeChildRun(childId, cancellationToken).ConfigureAwait(false);
            }
            await Task.Delay(
                options.PollInterval,
                timeProvider,
                cancellationToken).ConfigureAwait(false);
        }
    }

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
