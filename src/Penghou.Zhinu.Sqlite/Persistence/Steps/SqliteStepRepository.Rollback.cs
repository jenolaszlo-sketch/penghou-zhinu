using Microsoft.Data.Sqlite;
using System.Text.Json;
using Penghou.Zhinu.Sqlite.Persistence.Leases;
using Penghou.Zhinu.Sqlite.Persistence.Workflows;

namespace Penghou.Zhinu.Sqlite.Persistence.Steps;

internal sealed partial class SqliteStepRepository
{
    public async ValueTask<RollbackPlan> PlanRollbackAsync(
        Guid workflowRunId,
        string? targetStepKey,
        RollbackBoundary boundary,
        CancellationToken cancellationToken = default)
    {
        if (workflowRunId == Guid.Empty)
            throw new ArgumentException("Workflow ID must not be empty.", nameof(workflowRunId));
        if (targetStepKey is not null)
            ArgumentException.ThrowIfNullOrWhiteSpace(targetStepKey);
        await factory.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await factory.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var steps = await getCurrentSteps.ExecuteAsync(
            connection,
            transaction,
            workflowRunId,
            cancellationToken).ConfigureAwait(false);
        var dependencies = await getStepDependencies.ExecuteAsync(
            connection,
            transaction,
            workflowRunId,
            cancellationToken).ConfigureAwait(false);
        var compensations = await getCompensations.ExecuteAsync(
            connection,
            transaction,
            workflowRunId,
            cancellationToken).ConfigureAwait(false);
        var plan = ResolveRollbackPlan(
            workflowRunId,
            targetStepKey,
            boundary,
            steps,
            dependencies,
            compensations);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return plan;
    }

    public async ValueTask<long?> ClaimRollbackAsync(
        Guid workflowRunId,
        string ownerId,
        DateTimeOffset now,
        DateTimeOffset leaseExpiresAt,
        CancellationToken cancellationToken = default)
    {
        await factory.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await factory.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var generation = await claimRollback.ExecuteAsync(
            connection,
            transaction,
            workflowRunId,
            ownerId,
            now,
            leaseExpiresAt,
            cancellationToken).ConfigureAwait(false);
        if (generation is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return generation;
    }

    public async ValueTask<bool> RenewRollbackLeaseAsync(
        Guid workflowRunId,
        string ownerId,
        DateTimeOffset leaseExpiresAt,
        CancellationToken cancellationToken = default)
    {
        await factory.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await factory.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        return await renewRollbackLease.ExecuteAsync(
            connection,
            workflowRunId,
            ownerId,
            leaseExpiresAt,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask ReleaseRollbackLeaseAsync(
        Guid workflowRunId,
        string ownerId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await factory.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await factory.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        await releaseRollbackLease.ExecuteAsync(
            connection,
            workflowRunId,
            ownerId,
            now,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<bool> CompleteRollbackAsync(
        Guid workflowRunId,
        string ownerId,
        long generation,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await factory.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await factory.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        if (await completeRollback.ExecuteAsync(
            connection,
            transaction,
            workflowRunId,
            ownerId,
            generation,
            now,
            cancellationToken).ConfigureAwait(false) != 1)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async ValueTask FailRollbackAsync(
        Guid workflowRunId,
        string ownerId,
        long generation,
        WorkflowError error,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await factory.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await factory.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        if (await failRollback.ExecuteAsync(
            connection,
            transaction,
            workflowRunId,
            ownerId,
            generation,
            error,
            now,
            cancellationToken).ConfigureAwait(false) != 1)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return;
        }
        await insertEvent.ExecuteAsync(
            connection,
            transaction,
            workflowRunId,
            null,
            WorkflowEventTypes.WorkflowFailed,
            now,
            null,
            JsonSerializer.Serialize(error, SqliteStoreSupport.SerializerOptions),
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static RollbackPlan ResolveRollbackPlan(
        Guid workflowRunId,
        string? targetStepKey,
        RollbackBoundary boundary,
        IReadOnlyList<WorkflowStepRun> steps,
        IReadOnlyList<StepDependency> dependencies,
        IReadOnlyList<WorkflowStepCompensation> compensations)
    {
        var stepKeys = steps
            .Select(step => step.StepKey)
            .ToHashSet(StringComparer.Ordinal);
        var claimableByKey = new Dictionary<string, WorkflowStepCompensation>(
            StringComparer.Ordinal);
        foreach (var row in compensations.OrderBy(item => item.Revision))
        {
            if (stepKeys.Contains(row.StepKey) &&
                row.InputJson is not null &&
                row.Status is CompensationStatus.Pending or CompensationStatus.Failed)
            {
                claimableByKey[row.StepKey] = row;
            }
        }

        bool Compensable(string stepKey) =>
            claimableByKey.ContainsKey(stepKey);

        var topoOrder = TopologicalOrder(steps, dependencies);
        var topoIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < topoOrder.Count; index++)
            topoIndex[topoOrder[index].StepKey] = index;

        var compensated = new List<RollbackPlanStep>();
        var preserved = new List<RollbackPlanStep>();

        if (targetStepKey is null)
        {
            foreach (var step in topoOrder)
            {
                if (Compensable(step.StepKey))
                {
                    compensated.Add(new RollbackPlanStep(
                        step.StepKey,
                        RollbackAction.Compensate,
                        RollbackReason.Dependent));
                }
            }
            foreach (var step in steps
                .OrderBy(item => item.CreatedAt)
                .ThenBy(item => item.StepKey, StringComparer.Ordinal))
            {
                if (!Compensable(step.StepKey))
                {
                    preserved.Add(new RollbackPlanStep(
                        step.StepKey,
                        RollbackAction.Preserve,
                        RollbackReason.IndependentBranch));
                }
            }
            compensated.Sort((a, b) =>
                topoIndex[b.StepKey].CompareTo(topoIndex[a.StepKey]));
            return new RollbackPlan(
                workflowRunId,
                null,
                boundary,
                compensated.Concat(preserved).ToArray());
        }

        var dependents = new HashSet<string>(StringComparer.Ordinal);
        var ancestors = new HashSet<string>(StringComparer.Ordinal);
        if (Compensable(targetStepKey) ||
            steps.Any(step => step.StepKey == targetStepKey))
        {
            var dependentsOf = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var dependsOn = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var dependency in dependencies)
            {
                if (!stepKeys.Contains(dependency.StepKey) ||
                    !stepKeys.Contains(dependency.DependsOnStepKey))
                {
                    continue;
                }
                if (!dependentsOf.TryGetValue(
                        dependency.DependsOnStepKey,
                        out var dependentsList))
                {
                    dependentsList = dependentsOf[dependency.DependsOnStepKey] = [];
                }
                dependentsList.Add(dependency.StepKey);
                if (!dependsOn.TryGetValue(dependency.StepKey, out var ancestorsList))
                {
                    ancestorsList = dependsOn[dependency.StepKey] = [];
                }
                ancestorsList.Add(dependency.DependsOnStepKey);
            }
            dependents = TransitiveClosure(dependentsOf, targetStepKey);
            ancestors = TransitiveClosure(dependsOn, targetStepKey);
        }

        foreach (var step in steps
            .OrderBy(item => item.CreatedAt)
            .ThenBy(item => item.StepKey, StringComparer.Ordinal))
        {
            if (string.Equals(step.StepKey, targetStepKey, StringComparison.Ordinal))
            {
                if (boundary == RollbackBoundary.BeforeStep)
                {
                    compensated.Add(new RollbackPlanStep(
                        step.StepKey,
                        RollbackAction.Compensate,
                        RollbackReason.Boundary));
                }
                else
                {
                    preserved.Add(new RollbackPlanStep(
                        step.StepKey,
                        RollbackAction.Preserve,
                        RollbackReason.Boundary));
                }
            }
            else if (dependents.Contains(step.StepKey))
            {
                if (Compensable(step.StepKey))
                {
                    compensated.Add(new RollbackPlanStep(
                        step.StepKey,
                        RollbackAction.Compensate,
                        RollbackReason.Dependent));
                }
                else
                {
                    preserved.Add(new RollbackPlanStep(
                        step.StepKey,
                        RollbackAction.Preserve,
                        RollbackReason.Dependent));
                }
            }
            else if (ancestors.Contains(step.StepKey))
            {
                preserved.Add(new RollbackPlanStep(
                    step.StepKey,
                    RollbackAction.Preserve,
                    RollbackReason.Ancestor));
            }
            else
            {
                preserved.Add(new RollbackPlanStep(
                    step.StepKey,
                    RollbackAction.Preserve,
                    RollbackReason.IndependentBranch));
            }
        }

        compensated.Sort((a, b) =>
            topoIndex[b.StepKey].CompareTo(topoIndex[a.StepKey]));
        return new RollbackPlan(
            workflowRunId,
            targetStepKey,
            boundary,
            compensated.Concat(preserved).ToArray());
    }

    private static HashSet<string> TransitiveClosure(
        Dictionary<string, List<string>> adjacency,
        string start)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        queue.Enqueue(start);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!adjacency.TryGetValue(current, out var neighbors))
                continue;
            foreach (var neighbor in neighbors)
            {
                if (visited.Add(neighbor))
                    queue.Enqueue(neighbor);
            }
        }
        visited.Remove(start);
        return visited;
    }

    private static List<WorkflowStepRun> TopologicalOrder(
        IReadOnlyList<WorkflowStepRun> steps,
        IReadOnlyList<StepDependency> dependencies)
    {
        var byKey = steps.ToDictionary(
            step => step.StepKey,
            StringComparer.Ordinal);
        var dependsOn = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var dependency in dependencies)
        {
            if (!byKey.ContainsKey(dependency.StepKey) ||
                !byKey.ContainsKey(dependency.DependsOnStepKey))
            {
                continue;
            }
            if (!dependsOn.TryGetValue(dependency.StepKey, out var list))
            {
                list = dependsOn[dependency.StepKey] = [];
            }
            list.Add(dependency.DependsOnStepKey);
        }
        var ordered = new List<WorkflowStepRun>(steps.Count);
        var resolved = new HashSet<string>(StringComparer.Ordinal);
        var remaining = steps
            .OrderBy(step => step.CreatedAt)
            .ThenBy(step => step.StepKey, StringComparer.Ordinal)
            .ToList();
        while (remaining.Count > 0)
        {
            var next = remaining.FirstOrDefault(step =>
                !dependsOn.TryGetValue(step.StepKey, out var deps) ||
                deps.All(dependency =>
                    !byKey.ContainsKey(dependency) ||
                    resolved.Contains(dependency)));
            if (next is null)
            {
                ordered.AddRange(remaining);
                break;
            }
            ordered.Add(next);
            resolved.Add(next.StepKey);
            remaining.Remove(next);
        }
        return ordered;
    }
}
