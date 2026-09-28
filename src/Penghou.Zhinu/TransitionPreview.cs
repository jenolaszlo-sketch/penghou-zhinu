namespace Penghou.Zhinu;

/// <summary>
/// One candidate step in Zhinu step terms, as supplied by the planning layer.
/// Fuwen plan paths map to step keys in the adapter; Zhinu never interprets
/// planning semantics beyond these durable identities.
/// </summary>
public sealed record CandidateStep
{
    public required string StepKey { get; init; }
    public string? InputHash { get; init; }
    public string? ImplementationKey { get; init; }
    public IReadOnlyList<string>? DependsOn { get; init; }
}

/// <summary>How one structural node differs between the current and candidate plans.</summary>
public enum PlanNodeTransition
{
    /// <summary>Identical key, inputs, implementation, and dependencies.</summary>
    Unchanged,
    /// <summary>Present only in the candidate.</summary>
    Added,
    /// <summary>Present only in the current plan.</summary>
    Removed,
    /// <summary>Same key with different effective inputs.</summary>
    ChangedInputs,
    /// <summary>Same key and inputs with a different implementation.</summary>
    ChangedImplementation,
    /// <summary>Same key, inputs, and implementation with different dependencies.</summary>
    ChangedDependencies
}

/// <summary>One node's verdict within a transition preview.</summary>
public sealed record PlanNodePreview(string StepKey, PlanNodeTransition Transition);

/// <summary>
/// A calculated transition preview: per-node verdicts plus the reuse,
/// invalidation, and cancellation sets a cutover would apply. Recording the
/// preview authorizes nothing; activation stays explicit.
/// </summary>
public sealed record WorkflowTransitionPreview
{
    public required IReadOnlyList<PlanNodePreview> Nodes { get; init; }
    public required IReadOnlyList<string> ReusableSteps { get; init; }
    public required IReadOnlyList<string> InvalidatedSteps { get; init; }
    public required IReadOnlyList<string> CancelledSteps { get; init; }
    public required IReadOnlyList<string> AddedSteps { get; init; }
}

/// <summary>
/// Calculates transition previews from current durable step state plus a
/// candidate supplied in the same terms. Pure and deterministic: no store
/// access, no side effects. Unknown (null) hashes compare equal to each
/// other and different from any value.
/// </summary>
public static class TransitionPreview
{
    public static WorkflowTransitionPreview Calculate(
        IReadOnlyList<WorkflowStepRun> currentSteps,
        IReadOnlyList<StepDependency> currentDependencies,
        IReadOnlyList<CandidateStep> candidateSteps)
    {
        ArgumentNullException.ThrowIfNull(currentSteps);
        ArgumentNullException.ThrowIfNull(currentDependencies);
        ArgumentNullException.ThrowIfNull(candidateSteps);

        var current = IndexSteps(currentSteps);
        var candidates = IndexCandidates(candidateSteps);
        var currentDependents = BuildDependents(currentDependencies);
        var currentEdges = BuildEdges(currentDependencies);

        var nodes = new List<PlanNodePreview>();
        var invalidated = new HashSet<string>(StringComparer.Ordinal);
        var unchangedCompleted = new List<string>();
        var added = new List<string>();
        foreach (var key in current.Keys
            .Concat(candidates.Keys)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal))
        {
            current.TryGetValue(key, out var currentStep);
            candidates.TryGetValue(key, out var candidate);
            currentEdges.TryGetValue(key, out var edges);
            var transition = Classify(currentStep, candidate, edges);
            nodes.Add(new PlanNodePreview(key, transition));
            if (transition == PlanNodeTransition.Added)
            {
                added.Add(key);
            }
            else if (transition != PlanNodeTransition.Unchanged)
            {
                invalidated.Add(key);
            }
            else if (currentStep!.Status == StepStatus.Completed)
            {
                unchangedCompleted.Add(key);
            }
        }

        // Invalidation is transitive through durable dependents; reuse is not:
        // an unchanged completed step with an invalidated dependency re-runs.
        var queue = new Queue<string>(invalidated);
        while (queue.Count > 0)
        {
            var invalid = queue.Dequeue();
            if (!currentDependents.TryGetValue(invalid, out var dependents))
                continue;
            foreach (var dependent in dependents)
            {
                if (invalidated.Add(dependent))
                    queue.Enqueue(dependent);
            }
        }

        var reusable = unchangedCompleted
            .Where(key => !invalidated.Contains(key))
            .Order(StringComparer.Ordinal)
            .ToList();

        var cancelled = invalidated
            .Where(key => current.TryGetValue(key, out var step) &&
                step.Status is StepStatus.Running or StepStatus.Waiting)
            .Order(StringComparer.Ordinal)
            .ToList();

        return new WorkflowTransitionPreview
        {
            Nodes = nodes,
            ReusableSteps = reusable.Order(StringComparer.Ordinal).ToList(),
            InvalidatedSteps = invalidated.Order(StringComparer.Ordinal).ToList(),
            CancelledSteps = cancelled,
            AddedSteps = added
        };
    }

    private static Dictionary<string, WorkflowStepRun> IndexSteps(
        IReadOnlyList<WorkflowStepRun> steps)
    {
        var indexed = new Dictionary<string, WorkflowStepRun>(StringComparer.Ordinal);
        foreach (var step in steps)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(step.StepKey, nameof(steps));
            if (!indexed.TryAdd(step.StepKey, step))
                throw new ArgumentException(
                    $"Duplicate current step key '{step.StepKey}'.", nameof(steps));
        }
        return indexed;
    }

    private static Dictionary<string, CandidateStep> IndexCandidates(
        IReadOnlyList<CandidateStep> candidates)
    {
        var indexed = new Dictionary<string, CandidateStep>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            ArgumentNullException.ThrowIfNull(candidate);
            ArgumentException.ThrowIfNullOrWhiteSpace(candidate.StepKey, nameof(candidates));
            if (!indexed.TryAdd(candidate.StepKey, candidate))
                throw new ArgumentException(
                    $"Duplicate candidate step key '{candidate.StepKey}'.", nameof(candidates));
        }
        return indexed;
    }

    private static Dictionary<string, List<string>> BuildDependents(
        IReadOnlyList<StepDependency> dependencies)
    {
        var dependents = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var edge in dependencies)
        {
            if (!dependents.TryGetValue(edge.DependsOnStepKey, out var list))
                dependents[edge.DependsOnStepKey] = list = [];
            if (!list.Contains(edge.StepKey, StringComparer.Ordinal))
                list.Add(edge.StepKey);
        }
        return dependents;
    }

    private static Dictionary<string, HashSet<string>> BuildEdges(
        IReadOnlyList<StepDependency> dependencies)
    {
        var edges = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var edge in dependencies)
        {
            if (!edges.TryGetValue(edge.StepKey, out var set))
                edges[edge.StepKey] = set = new HashSet<string>(StringComparer.Ordinal);
            set.Add(edge.DependsOnStepKey);
        }
        return edges;
    }

    private static PlanNodeTransition Classify(
        WorkflowStepRun? current,
        CandidateStep? candidate,
        HashSet<string>? currentEdges)
    {
        if (current is null)
            return PlanNodeTransition.Added;
        if (candidate is null)
            return PlanNodeTransition.Removed;
        if (!HashesEqual(current.InputHash, candidate.InputHash))
            return PlanNodeTransition.ChangedInputs;
        if (!HashesEqual(current.ImplementationKey, candidate.ImplementationKey))
            return PlanNodeTransition.ChangedImplementation;
        var candidateEdges = candidate.DependsOn is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(candidate.DependsOn, StringComparer.Ordinal);
        var existing = currentEdges ?? new HashSet<string>(StringComparer.Ordinal);
        return candidateEdges.SetEquals(existing)
            ? PlanNodeTransition.Unchanged
            : PlanNodeTransition.ChangedDependencies;
    }

    private static bool HashesEqual(string? left, string? right) =>
        left is null ? right is null : string.Equals(left, right, StringComparison.Ordinal);
}
