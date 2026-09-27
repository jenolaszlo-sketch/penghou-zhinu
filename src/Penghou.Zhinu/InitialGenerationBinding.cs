namespace Penghou.Zhinu;

/// <summary>
/// Shared initial-admission boundary for root, forked, and child runs: every
/// run opens its own instance with an active first generation, and idempotent
/// retries heal missing bindings or complete an interrupted initial admission.
/// Review candidates are never promoted; they fail explicitly.
/// </summary>
internal static class InitialGenerationBinding
{
    public static async ValueTask BindAsync(
        IWorkflowStore store,
        Guid workflowRunId,
        string? planRevision,
        string workflowName,
        string workflowVersion,
        CancellationToken cancellationToken)
    {
        // A started, forked, or child run opens its own instance: exactly one
        // generation owns forward progression per instance, so sharing another
        // run's instance would violate single ownership.
        var instance = await store.CreateInstanceAsync(
            System.Text.Json.JsonSerializer.Serialize(
                new { workflowName, workflowVersion }),
            cancellationToken).ConfigureAwait(false);
        var created = await store.CreateGenerationAsync(
            instance.InstanceId, workflowRunId, planRevision, null, null,
            cancellationToken).ConfigureAwait(false);
        var prepared = await store.PrepareGenerationAsync(
            created.GenerationId, cancellationToken).ConfigureAwait(false);
        await store.ActivateGenerationAsync(
            prepared.GenerationId, null, cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask EnsureAsync(
        IWorkflowStore store,
        Guid workflowRunId,
        string? planRevision,
        string workflowName,
        string workflowVersion,
        CancellationToken cancellationToken)
    {
        var bound = await store.GetGenerationByRunAsync(workflowRunId, cancellationToken)
            .ConfigureAwait(false);
        if (bound is null)
        {
            await BindAsync(
                store, workflowRunId, planRevision, workflowName, workflowVersion,
                cancellationToken).ConfigureAwait(false);
            return;
        }
        if (bound.Status is WorkflowGenerationStatus.Active or WorkflowGenerationStatus.Quiescing)
            return;
        if (!await IsInterruptedInitialAdmissionAsync(store, bound, planRevision, cancellationToken)
            .ConfigureAwait(false))
            throw new WorkflowStateException(
                $"Workflow run '{workflowRunId:D}' has a prepared generation that is not an " +
                "interrupted initial admission; admission does not promote review candidates. " +
                "Resolve it explicitly before retrying.");
        var admitted = bound.Status == WorkflowGenerationStatus.Created
            ? await store.PrepareGenerationAsync(bound.GenerationId, cancellationToken)
                .ConfigureAwait(false)
            : bound;
        try
        {
            await store.ActivateGenerationAsync(admitted.GenerationId, null, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (WorkflowStateException)
        {
            // Lost the activation race: converge when a concurrent identical
            // retry finished it, otherwise surface the conflict.
            var current = await store.GetGenerationByRunAsync(workflowRunId, cancellationToken)
                .ConfigureAwait(false);
            if (current?.GenerationId != admitted.GenerationId ||
                current.Status is not (WorkflowGenerationStatus.Active or WorkflowGenerationStatus.Quiescing))
                throw;
        }
    }

    /// <summary>
    /// An interrupted initial admission is the sole generation of its
    /// instance, ordinal one, predecessorless, and bound to the admitted
    /// definition's plan revision. The generation row itself is the admission
    /// record.
    /// </summary>
    private static async ValueTask<bool> IsInterruptedInitialAdmissionAsync(
        IWorkflowStore store,
        WorkflowGeneration bound,
        string? planRevision,
        CancellationToken cancellationToken)
    {
        if (bound.PredecessorGenerationId is not null || bound.Ordinal != 1)
            return false;
        if (!string.Equals(bound.PlanRevision, planRevision, StringComparison.Ordinal))
            return false;
        var siblings = await store.ListGenerationsAsync(bound.InstanceId, cancellationToken)
            .ConfigureAwait(false);
        return siblings.Count == 1;
    }
}
