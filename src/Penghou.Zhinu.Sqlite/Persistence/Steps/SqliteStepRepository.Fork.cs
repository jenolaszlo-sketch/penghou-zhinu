using Microsoft.Data.Sqlite;
using System.Text.Json;
using Penghou.Zhinu.Sqlite.Persistence.Leases;
using Penghou.Zhinu.Sqlite.Persistence.Workflows;

namespace Penghou.Zhinu.Sqlite.Persistence.Steps;

internal sealed partial class SqliteStepRepository
{
    public async ValueTask<ForkPlan> PlanForkAsync(
        Guid sourceWorkflowRunId,
        string targetStepKey,
        StepRestartMode mode,
        CancellationToken cancellationToken = default)
    {
        ValidateForkArguments(sourceWorkflowRunId, targetStepKey);
        await factory.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await factory.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var plan = await ResolveForkPlanAsync(
            connection,
            transaction,
            sourceWorkflowRunId,
            targetStepKey,
            mode,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return plan;
    }

    public async ValueTask<ForkPlan> ForkRunAsync(
        Guid sourceWorkflowRunId,
        WorkflowRun newRun,
        string targetStepKey,
        StepRestartMode mode,
        string? actor,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        ValidateForkArguments(sourceWorkflowRunId, targetStepKey);
        ArgumentNullException.ThrowIfNull(newRun);
        if (newRun.Id == Guid.Empty)
            throw new ArgumentException("New workflow ID must not be empty.", nameof(newRun));
        if (newRun.Id == sourceWorkflowRunId)
            throw new ArgumentException("A fork must use a new workflow ID.", nameof(newRun));
        if (newRun.SourceRunId != sourceWorkflowRunId)
            throw new ArgumentException("The new run must identify its source run.", nameof(newRun));

        await factory.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await factory.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var source = await getRun.ExecuteAsync(
            connection,
            transaction,
            sourceWorkflowRunId,
            cancellationToken).ConfigureAwait(false) ??
            throw new WorkflowNotFoundException(
                $"Workflow '{sourceWorkflowRunId:D}' does not exist.");
        if (await getRun.ExecuteAsync(
                connection,
                transaction,
                newRun.Id,
                cancellationToken).ConfigureAwait(false) is not null)
        {
            throw new WorkflowStateException(
                $"Workflow run ID '{newRun.Id:D}' already exists.");
        }
        ValidateForkContract(source, newRun);
        var plan = await ResolveForkPlanAsync(
            connection,
            transaction,
            sourceWorkflowRunId,
            targetStepKey,
            mode,
            cancellationToken).ConfigureAwait(false);
        await insertRun.ExecuteAsync(connection, transaction, newRun, cancellationToken)
            .ConfigureAwait(false);

        var reusable = plan.StepsToReuse.ToHashSet(StringComparer.Ordinal);
        var sourceSteps = await getCurrentSteps.ExecuteAsync(
            connection,
            transaction,
            sourceWorkflowRunId,
            cancellationToken).ConfigureAwait(false);
        foreach (var sourceStep in sourceSteps.Where(step => reusable.Contains(step.StepKey)))
        {
            await insertStep.ExecuteAsync(
                connection,
                transaction,
                CopyCompletedStep(sourceStep, newRun),
                cancellationToken).ConfigureAwait(false);
        }
        var dependencies = await getStepDependencies.ExecuteAsync(
            connection,
            transaction,
            sourceWorkflowRunId,
            cancellationToken).ConfigureAwait(false);
        foreach (var dependency in dependencies.Where(edge =>
                     reusable.Contains(edge.StepKey) &&
                     reusable.Contains(edge.DependsOnStepKey)))
        {
            await insertStepDependency.ExecuteAsync(
                connection,
                transaction,
                newRun.Id,
                dependency.StepKey,
                dependency.DependsOnStepKey,
                newRun.CreatedAt,
                cancellationToken).ConfigureAwait(false);
        }
        await insertEvent.ExecuteAsync(
            connection,
            transaction,
            newRun.Id,
            targetStepKey,
            WorkflowEventTypes.RunForked,
            newRun.CreatedAt,
            null,
            JsonSerializer.Serialize(
                new
                {
                    sourceWorkflowRunId,
                    targetStepKey,
                    mode = mode.ToString(),
                    actor,
                    reason,
                    reusedSteps = plan.StepsToReuse,
                    reexecutedSteps = plan.StepsToReexecute
                },
                SqliteStoreSupport.SerializerOptions),
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return plan;
    }

    private async ValueTask<ForkPlan> ResolveForkPlanAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid sourceWorkflowRunId,
        string targetStepKey,
        StepRestartMode mode,
        CancellationToken cancellationToken)
    {
        // Mutation extension point: when the target step does not exist in
        // the source run (it is new in the destination definition), no
        // source step is invalidated and every completed source step is a
        // reuse candidate. The classic restart path still rejects unknown
        // steps; only fork previews and fork creation take this branch.
        var target = await getStep.ExecuteAsync(
            connection,
            transaction,
            sourceWorkflowRunId,
            targetStepKey,
            cancellationToken).ConfigureAwait(false);
        Dictionary<string, RestartReason> invalidated;
        if (target is null)
        {
            invalidated = new Dictionary<string, RestartReason>(StringComparer.Ordinal);
        }
        else
        {
            var restart = await ResolveRestartPlanAsync(
                connection,
                transaction,
                sourceWorkflowRunId,
                targetStepKey,
                mode,
                cancellationToken).ConfigureAwait(false);
            invalidated = restart.StepsToInvalidate.ToDictionary(
                step => step.StepKey,
                step => step.Reason,
                StringComparer.Ordinal);
        }
        var steps = await getCurrentSteps.ExecuteAsync(
            connection,
            transaction,
            sourceWorkflowRunId,
            cancellationToken).ConfigureAwait(false);
        var reusable = steps
            .Where(step => step.Status == StepStatus.Completed &&
                           !invalidated.ContainsKey(step.StepKey))
            .Select(step => step.StepKey)
            .ToArray();
        var reexecute = steps
            .Where(step => !reusable.Contains(step.StepKey, StringComparer.Ordinal))
            .Select(step => new ForkPlanStep(
                step.StepKey,
                invalidated.TryGetValue(step.StepKey, out var restartReason)
                    ? MapForkReason(restartReason)
                    : ForkStepReason.NotCompleted))
            .ToArray();
        return new ForkPlan(
            sourceWorkflowRunId,
            targetStepKey,
            mode,
            reusable,
            reexecute);
    }

    private static WorkflowStepRun CopyCompletedStep(
        WorkflowStepRun source,
        WorkflowRun destination) => new()
        {
            Id = Guid.NewGuid(),
            WorkflowRunId = destination.Id,
            StepKey = source.StepKey,
            ImplementationKey = source.ImplementationKey,
            Status = StepStatus.Completed,
            Attempt = source.Attempt,
            InputJson = source.InputJson,
            InputType = source.InputType,
            InputHash = source.InputHash,
            OutputJson = source.OutputJson,
            OutputType = source.OutputType,
            SignalName = source.SignalName,
            CreatedAt = source.CreatedAt,
            StartedAt = source.StartedAt,
            CompletedAt = source.CompletedAt,
            Revision = 1,
            LeaseGeneration = destination.LeaseGeneration,
            AuthorizationDeclarationHash = source.AuthorizationDeclarationHash
        };

    private static ForkStepReason MapForkReason(RestartReason reason) =>
        reason switch
        {
            RestartReason.Requested => ForkStepReason.Requested,
            RestartReason.Dependent => ForkStepReason.Dependent,
            _ => throw new ArgumentOutOfRangeException(nameof(reason))
        };

    private static void ValidateForkArguments(
        Guid sourceWorkflowRunId,
        string targetStepKey)
    {
        if (sourceWorkflowRunId == Guid.Empty)
        {
            throw new ArgumentException(
                "Workflow ID must not be empty.",
                nameof(sourceWorkflowRunId));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(targetStepKey);
    }

    private static void ValidateForkContract(
        WorkflowRun source,
        WorkflowRun destination)
    {
        // Versioned mutation binds the destination to a different workflow
        // name/version than the source; the engine guarantees the target
        // registration preserves the input/output contract. The store still
        // requires a pending destination with equal input value and types.
        if (destination.Status != WorkflowStatus.Pending ||
            destination.InputType != source.InputType ||
            destination.InputJson != source.InputJson ||
            destination.OutputType != source.OutputType)
        {
            throw new WorkflowStateException(
                "A fork must preserve the source input value and the input/output contract.");
        }
    }
}
