namespace Penghou.Zhinu;

/// <summary>
/// Represents an explicit successful control decision for one durable loop
/// body. Outcomes are created by the active
/// <see cref="WorkflowLoopIteration{TState}"/> so they cannot be transferred
/// between loop scopes.
/// </summary>
public sealed class LoopBodyOutcome<TState>
{
    private readonly object scope;

    internal LoopBodyOutcome(
        object scope,
        LoopBodyOutcomeKind kind,
        TState state)
    {
        this.scope = scope;
        Kind = kind;
        State = state;
    }

    /// <summary>The successful control decision for the current iteration.</summary>
    public LoopBodyOutcomeKind Kind { get; }

    /// <summary>The immutable state selected by the body.</summary>
    public TState State { get; }

    internal bool BelongsTo(object expectedScope) =>
        ReferenceEquals(scope, expectedScope);
}
