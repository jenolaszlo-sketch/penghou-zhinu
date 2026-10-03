using Microsoft.Data.Sqlite;
using System.Text.Json;
using Penghou.Zhinu.Sqlite.Persistence.Leases;
using Penghou.Zhinu.Sqlite.Persistence.Workflows;

namespace Penghou.Zhinu.Sqlite.Persistence.Steps;

internal sealed partial class SqliteStepRepository
{
    public async ValueTask<RestartPlan> PlanRestartAsync(
        Guid workflowRunId,
        string stepKey,
        StepRestartMode mode,
        CancellationToken cancellationToken = default)
    {
        if (workflowRunId == Guid.Empty)
            throw new ArgumentException("Workflow ID must not be empty.", nameof(workflowRunId));
        ArgumentException.ThrowIfNullOrWhiteSpace(stepKey);
        await factory.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await factory.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var plan = await ResolveRestartPlanAsync(
            connection,
            transaction,
            workflowRunId,
            stepKey,
            mode,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return plan;
    }

    public async ValueTask<RestartPlan> RestartStepAsync(
        Guid workflowRunId,
        string stepKey,
        StepRestartMode mode,
        string? actor,
        string? reason,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        if (workflowRunId == Guid.Empty)
            throw new ArgumentException("Workflow ID must not be empty.", nameof(workflowRunId));
        ArgumentException.ThrowIfNullOrWhiteSpace(stepKey);
        await factory.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await factory.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var applied = await ApplyRestartAsync(
            connection,
            transaction,
            workflowRunId,
            stepKey,
            mode,
            operationId: null,
            actor,
            reason,
            now,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return applied.Plan;
    }

    public async ValueTask<RestartReceipt> RestartStepIdempotentlyAsync(
        Guid workflowRunId,
        string stepKey,
        StepRestartMode mode,
        Guid operationId,
        string? actor,
        string? reason,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        if (workflowRunId == Guid.Empty)
            throw new ArgumentException("Workflow ID must not be empty.", nameof(workflowRunId));
        if (operationId == Guid.Empty)
            throw new ArgumentException("Operation ID must not be empty.", nameof(operationId));
        ArgumentException.ThrowIfNullOrWhiteSpace(stepKey);

        await factory.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await factory.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);

        var existing = await GetOperationByIdAsync(
            connection,
            transaction,
            operationId,
            cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            var existingReceipt = ReadRestartReceipt(
                existing,
                workflowRunId,
                stepKey,
                mode,
                actor,
                reason);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return existingReceipt with { WasApplied = false };
        }

        var applied = await ApplyRestartAsync(
            connection,
            transaction,
            workflowRunId,
            stepKey,
            mode,
            operationId,
            actor,
            reason,
            now,
            cancellationToken).ConfigureAwait(false);
        var payload = new RestartOperationPayload
        {
            StepKey = stepKey,
            Mode = mode,
            Actor = actor,
            Reason = reason,
            LeaseGeneration = applied.LeaseGeneration,
            Event = applied.Event,
            StepsToInvalidate = applied.Plan.StepsToInvalidate.ToArray()
        };
        await insertOperation.ExecuteAsync(
            connection,
            transaction,
            new WorkflowRunOperation
            {
                OperationId = operationId,
                WorkflowRunId = workflowRunId,
                OperationType = StepRestartOperationType,
                Status = WorkflowOperationStatus.Completed,
                PayloadJson = JsonSerializer.Serialize(
                    payload,
                    SqliteStoreSupport.SerializerOptions),
                CreatedAt = now,
                UpdatedAt = now,
                CompletedAt = now
            },
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return CreateRestartReceipt(
            operationId,
            workflowRunId,
            payload,
            wasApplied: true);
    }

    private async ValueTask<RestartPlan> ResolveRestartPlanAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid workflowRunId,
        string stepKey,
        StepRestartMode mode,
        CancellationToken cancellationToken)
    {
        var runStatus = await getRunStatus.ExecuteAsync(
            connection,
            transaction,
            workflowRunId,
            cancellationToken).ConfigureAwait(false);
        if (runStatus is null)
        {
            throw new WorkflowNotFoundException(
                $"Workflow '{workflowRunId:D}' does not exist.");
        }
        var target = await getStep.ExecuteAsync(
            connection,
            transaction,
            workflowRunId,
            stepKey,
            cancellationToken).ConfigureAwait(false) ??
            throw new WorkflowNotFoundException(
                $"Step '{stepKey}' does not exist in workflow '{workflowRunId:D}'.");
        var steps = await getCurrentSteps.ExecuteAsync(
            connection,
            transaction,
            workflowRunId,
            cancellationToken).ConfigureAwait(false);
        var invalidated = mode switch
        {
            StepRestartMode.StepOnly =>
                new List<RestartPlanStep>
                {
                    new(stepKey, RestartReason.Requested)
                },
            StepRestartMode.Dependents =>
                await ResolveDependentsAsync(
                    connection,
                    transaction,
                    steps,
                    workflowRunId,
                    stepKey,
                    target,
                    cancellationToken).ConfigureAwait(false),
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };
        return new RestartPlan(workflowRunId, stepKey, invalidated);
    }

    private async ValueTask<List<RestartPlanStep>> ResolveDependentsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<WorkflowStepRun> steps,
        Guid workflowRunId,
        string stepKey,
        WorkflowStepRun target,
        CancellationToken cancellationToken)
    {
        var dependencies = await getStepDependencies.ExecuteAsync(
            connection,
            transaction,
            workflowRunId,
            cancellationToken).ConfigureAwait(false);
        var dependentsOf = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var dependency in dependencies)
        {
            if (!dependentsOf.TryGetValue(
                    dependency.DependsOnStepKey,
                    out var list))
            {
                list = dependentsOf[dependency.DependsOnStepKey] = [];
            }
            list.Add(dependency.StepKey);
        }
        var visited = new HashSet<string>(StringComparer.Ordinal) { stepKey };
        var queue = new Queue<string>();
        queue.Enqueue(stepKey);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!dependentsOf.TryGetValue(current, out var dependents))
                continue;
            foreach (var dependent in dependents)
            {
                if (visited.Add(dependent))
                    queue.Enqueue(dependent);
            }
        }
        var ordered = steps
            .Where(step => visited.Contains(step.StepKey) && step.StepKey != stepKey)
            .OrderBy(step => step.CreatedAt)
            .ThenBy(step => step.StepKey, StringComparer.Ordinal)
            .Select(step => new RestartPlanStep(
                step.StepKey,
                RestartReason.Dependent))
            .ToList();
        ordered.Insert(0, new RestartPlanStep(stepKey, RestartReason.Requested));
        return ordered;
    }

    private async ValueTask<AppliedRestart> ApplyRestartAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid workflowRunId,
        string stepKey,
        StepRestartMode mode,
        Guid? operationId,
        string? actor,
        string? reason,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var plan = await ResolveRestartPlanAsync(
            connection,
            transaction,
            workflowRunId,
            stepKey,
            mode,
            cancellationToken).ConfigureAwait(false);
        await bumpRunGeneration.ExecuteAsync(
            connection,
            transaction,
            workflowRunId,
            cancellationToken).ConfigureAwait(false);
        var generation = await getRunLeaseGeneration.ExecuteAsync(
            connection,
            transaction,
            workflowRunId,
            cancellationToken).ConfigureAwait(false);
        await resetRunForRestart.ExecuteAsync(
            connection,
            transaction,
            workflowRunId,
            now,
            cancellationToken).ConfigureAwait(false);
        foreach (var entry in plan.StepsToInvalidate)
        {
            var latest = await getStep.ExecuteAsync(
                connection,
                transaction,
                workflowRunId,
                entry.StepKey,
                cancellationToken).ConfigureAwait(false) ??
                throw new WorkflowNotFoundException(
                    $"Step '{entry.StepKey}' does not exist in workflow '{workflowRunId:D}'.");
            var next = new WorkflowStepRun
            {
                Id = Guid.NewGuid(),
                WorkflowRunId = workflowRunId,
                StepKey = entry.StepKey,
                ImplementationKey = latest.ImplementationKey,
                Status = StepStatus.Pending,
                Attempt = 0,
                CreatedAt = now,
                InputJson = latest.InputJson,
                InputType = latest.InputType,
                InputHash = latest.InputHash,
                OutputType = latest.OutputType,
                SignalName = latest.SignalName,
                Revision = latest.Revision + 1,
                LeaseGeneration = generation,
                AuthorizationDeclarationHash = latest.AuthorizationDeclarationHash
            };
            await insertStep.ExecuteAsync(connection, transaction, next, cancellationToken)
                .ConfigureAwait(false);
        }

        var invalidatedSteps = plan.StepsToInvalidate
            .Select(item => new
            {
                stepKey = item.StepKey,
                reason = item.Reason.ToString()
            })
            .ToArray();
        object eventData = operationId is null
            ? new
            {
                stepKey,
                mode = mode.ToString(),
                actor,
                reason,
                leaseGeneration = generation,
                invalidatedSteps
            }
            : new
            {
                operationId = operationId.Value,
                stepKey,
                mode = mode.ToString(),
                actor,
                reason,
                leaseGeneration = generation,
                invalidatedSteps
            };
        var restartEvent = await insertEvent.ExecuteAsync(
            connection,
            transaction,
            workflowRunId,
            stepKey,
            WorkflowEventTypes.StepRestarted,
            now,
            null,
            JsonSerializer.Serialize(
                eventData,
                SqliteStoreSupport.SerializerOptions),
            cancellationToken).ConfigureAwait(false);
        return new AppliedRestart(plan, generation, restartEvent);
    }

    private static RestartReceipt ReadRestartReceipt(
        WorkflowRunOperation operation,
        Guid workflowRunId,
        string stepKey,
        StepRestartMode mode,
        string? actor,
        string? reason)
    {
        if (operation.WorkflowRunId != workflowRunId ||
            !string.Equals(
                operation.OperationType,
                StepRestartOperationType,
                StringComparison.Ordinal))
        {
            throw RestartConflict(operation.OperationId);
        }
        if (operation.Status != WorkflowOperationStatus.Completed ||
            operation.PayloadJson is null)
        {
            throw new WorkflowStateException(
                $"Restart operation '{operation.OperationId:D}' has no completed receipt.");
        }

        RestartOperationPayload payload;
        try
        {
            payload = JsonSerializer.Deserialize<RestartOperationPayload>(
                operation.PayloadJson,
                SqliteStoreSupport.SerializerOptions) ??
                throw new JsonException("The receipt payload was null.");
        }
        catch (JsonException)
        {
            throw new WorkflowStateException(
                $"Restart operation '{operation.OperationId:D}' has an invalid durable receipt.");
        }
        if (!string.Equals(payload.StepKey, stepKey, StringComparison.Ordinal) ||
            payload.Mode != mode ||
            !string.Equals(payload.Actor, actor, StringComparison.Ordinal) ||
            !string.Equals(payload.Reason, reason, StringComparison.Ordinal))
        {
            throw RestartConflict(operation.OperationId);
        }
        return CreateRestartReceipt(
            operation.OperationId,
            workflowRunId,
            payload,
            wasApplied: false);
    }

    private static RestartReceipt CreateRestartReceipt(
        Guid operationId,
        Guid workflowRunId,
        RestartOperationPayload payload,
        bool wasApplied) =>
        new()
        {
            OperationId = operationId,
            Plan = new RestartPlan(
                workflowRunId,
                payload.StepKey,
                payload.StepsToInvalidate),
            Mode = payload.Mode,
            LeaseGeneration = payload.LeaseGeneration,
            Event = payload.Event,
            Actor = payload.Actor,
            Reason = payload.Reason,
            WasApplied = wasApplied
        };

    private static WorkflowOperationConflictException RestartConflict(Guid operationId) =>
        new(
            operationId,
            $"Operation ID '{operationId:D}' is already bound to different durable intent.");

    private sealed record RestartOperationPayload
    {
        public required string StepKey { get; init; }

        public required StepRestartMode Mode { get; init; }

        public string? Actor { get; init; }

        public string? Reason { get; init; }

        public required long LeaseGeneration { get; init; }

        public required WorkflowEvent Event { get; init; }

        public required RestartPlanStep[] StepsToInvalidate { get; init; }
    }

    private sealed record AppliedRestart(
        RestartPlan Plan,
        long LeaseGeneration,
        WorkflowEvent Event);
}
