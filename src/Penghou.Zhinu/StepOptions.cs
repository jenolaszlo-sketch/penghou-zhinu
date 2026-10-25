namespace Penghou.Zhinu;

/// <summary>Configures retry and execution behavior for one durable step.</summary>
public sealed record StepOptions
{
    internal DurableLoopIterationIdentity? GroupExecutionScope { get; init; }
    internal string? GroupFanOutExecutionScope { get; init; }
    internal ActivityGroupFanOutItemClaim? FanOutClaim { get; init; }

    /// <summary>Optional per-item group declaration, consumed only by native fan-out at an explicit selection boundary.</summary>
    public ActivityGroupFanOutBinding? FanOutGroup { get; init; }

    /// <summary>Optional direct occurrence membership; never a control-flow or authorization declaration.</summary>
    public ActivityGroupStepBinding? Group { get; init; }
    /// <summary>Immutable declared authority and approved plan identity for the forward callback.</summary>
    public WorkflowAuthorizationDeclaration? Authorization { get; init; }

    /// <summary>Separate declarations for compensation; forward permission never covers compensation.</summary>
    public WorkflowAuthorizationDeclaration? CompensationAuthorization { get; init; }

    public RetryPolicy Retry { get; init; } = new();

    public TimeSpan? ExecutionTimeout { get; init; }

    /// <summary>
    /// Step keys this step depends on. Restarting a step invalidates it and the
    /// transitive set of steps that depend on it, without touching unrelated
    /// branches. Use <see cref="WorkflowContext.DependsOn"/> to declare
    /// dependencies for many steps at once.
    /// </summary>
    public IReadOnlyCollection<string>? DependsOn { get; init; }

    internal void Validate()
    {
        Retry.Validate();
        if (ExecutionTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(ExecutionTimeout));
    }
}
