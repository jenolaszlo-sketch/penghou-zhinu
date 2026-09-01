namespace Penghou.Zhinu;

/// <summary>
/// Stable structural identity of a durable loop. Runtime iterations are
/// selected with <see cref="Iteration"/> without exposing persistence keys.
/// </summary>
public sealed record WorkflowLoopReference
{
    private WorkflowLoopReference(
        string name,
        WorkflowLoopIterationReference? parentIteration)
    {
        DurableLoopScope.ValidateName(name, nameof(name));
        Name = name;
        ParentIteration = parentIteration;
        Depth = parentIteration is null ? 1 : parentIteration.Loop.Depth + 1;
    }

    /// <summary>The loop's local structural name.</summary>
    public string Name { get; }

    /// <summary>The owning iteration for a nested loop; null for a root loop.</summary>
    public WorkflowLoopIterationReference? ParentIteration { get; }

    /// <summary>One-based lexical nesting depth.</summary>
    public int Depth { get; }

    /// <summary>Human-readable structural path without persistence encoding.</summary>
    public string DisplayPath => ParentIteration is null
        ? Name
        : $"{ParentIteration.DisplayPath}.{Name}";

    /// <summary>Creates a reference to a root workflow loop.</summary>
    public static WorkflowLoopReference Root(string name) => new(name, null);

    /// <summary>Selects one one-based runtime iteration.</summary>
    public WorkflowLoopIterationReference Iteration(int number) => new(this, number);

    /// <summary>Selects the loop's durable final-result boundary.</summary>
    public WorkflowLoopStepReference FinalStep =>
        WorkflowLoopStepReference.Final(this);

    /// <summary>Selects the loop-limit failure boundary.</summary>
    public WorkflowLoopStepReference LimitStep =>
        WorkflowLoopStepReference.Limit(this);

    /// <summary>Selects the resolved deadline and budget boundary.</summary>
    public WorkflowLoopStepReference LimitsStep =>
        WorkflowLoopStepReference.Limits(this);

    internal DurableLoopScope ToDurableScope()
    {
        if (ParentIteration is null)
            return DurableLoopScope.Root(Name);

        var parentScope = ParentIteration.Loop.ToDurableScope();
        return parentScope.Nest(
            parentScope.Iteration(ParentIteration.Number),
            Name);
    }

    internal static WorkflowLoopReference Nested(
        WorkflowLoopIterationReference parentIteration,
        string name) => new(name, parentIteration);
}

/// <summary>Stable identity of one durable execution of a loop body.</summary>
public sealed record WorkflowLoopIterationReference
{
    internal WorkflowLoopIterationReference(
        WorkflowLoopReference loop,
        int number)
    {
        ArgumentNullException.ThrowIfNull(loop);
        if (number < 1)
            throw new ArgumentOutOfRangeException(nameof(number));
        Loop = loop;
        Number = number;
    }

    public WorkflowLoopReference Loop { get; }

    /// <summary>One-based iteration number.</summary>
    public int Number { get; }

    public string DisplayPath => $"{Loop.DisplayPath}[{Number}]";

    public WorkflowLoopStepReference ConditionStep =>
        WorkflowLoopStepReference.Condition(this);

    public WorkflowLoopStepReference CommitStep =>
        WorkflowLoopStepReference.Commit(this);

    public WorkflowLoopStepReference BodyStep(string name) =>
        WorkflowLoopStepReference.Body(this, name);

    /// <summary>Declares the structural identity of a lexically owned child loop.</summary>
    public WorkflowLoopReference NestedLoop(string name) =>
        WorkflowLoopReference.Nested(this, name);
}

/// <summary>The semantic durable boundary selected within a loop.</summary>
public enum WorkflowLoopStepKind
{
    Condition,
    Body,
    Commit,
    Limit,
    Final,
    Limits
}

/// <summary>
/// Typed reference to a loop boundary suitable for inspection and restart.
/// Its storage key remains an internal implementation detail.
/// </summary>
public sealed record WorkflowLoopStepReference
{
    private WorkflowLoopStepReference(
        WorkflowLoopReference loop,
        WorkflowLoopIterationReference? iteration,
        WorkflowLoopStepKind kind,
        string? bodyStepName)
    {
        Loop = loop;
        Iteration = iteration;
        Kind = kind;
        BodyStepName = bodyStepName;
    }

    public WorkflowLoopReference Loop { get; }

    public WorkflowLoopIterationReference? Iteration { get; }

    public WorkflowLoopStepKind Kind { get; }

    public string? BodyStepName { get; }

    public string DisplayPath => Kind switch
    {
        WorkflowLoopStepKind.Condition => $"{Iteration!.DisplayPath}.condition",
        WorkflowLoopStepKind.Body => $"{Iteration!.DisplayPath}.body.{BodyStepName}",
        WorkflowLoopStepKind.Commit => $"{Iteration!.DisplayPath}.commit",
        WorkflowLoopStepKind.Limit => $"{Loop.DisplayPath}.limit",
        WorkflowLoopStepKind.Final => $"{Loop.DisplayPath}.final",
        WorkflowLoopStepKind.Limits => $"{Loop.DisplayPath}.limits",
        _ => throw new WorkflowStateException($"Unknown loop step kind '{Kind}'.")
    };

    internal string StepKey
    {
        get
        {
            var scope = Loop.ToDurableScope();
            return Kind switch
            {
                WorkflowLoopStepKind.Condition =>
                    DurableLoopStepKeys.Condition(scope.Iteration(Iteration!.Number)),
                WorkflowLoopStepKind.Body =>
                    DurableLoopStepKeys.Body(scope.Iteration(Iteration!.Number), BodyStepName!),
                WorkflowLoopStepKind.Commit =>
                    DurableLoopStepKeys.Commit(scope.Iteration(Iteration!.Number)),
                WorkflowLoopStepKind.Limit => DurableLoopStepKeys.Limit(scope),
                WorkflowLoopStepKind.Final => scope.FinalStepKey,
                WorkflowLoopStepKind.Limits => DurableLoopStepKeys.Limits(scope),
                _ => throw new WorkflowStateException($"Unknown loop step kind '{Kind}'.")
            };
        }
    }

    internal static WorkflowLoopStepReference Condition(
        WorkflowLoopIterationReference iteration) =>
        new(iteration.Loop, iteration, WorkflowLoopStepKind.Condition, null);

    internal static WorkflowLoopStepReference Body(
        WorkflowLoopIterationReference iteration,
        string name)
    {
        DurableLoopScope.ValidateName(name, nameof(name));
        return new(iteration.Loop, iteration, WorkflowLoopStepKind.Body, name);
    }

    internal static WorkflowLoopStepReference Commit(
        WorkflowLoopIterationReference iteration) =>
        new(iteration.Loop, iteration, WorkflowLoopStepKind.Commit, null);

    internal static WorkflowLoopStepReference Limit(WorkflowLoopReference loop) =>
        new(loop, null, WorkflowLoopStepKind.Limit, null);

    internal static WorkflowLoopStepReference Limits(WorkflowLoopReference loop) =>
        new(loop, null, WorkflowLoopStepKind.Limits, null);

    internal static WorkflowLoopStepReference Final(WorkflowLoopReference loop) =>
        new(loop, null, WorkflowLoopStepKind.Final, null);
}
