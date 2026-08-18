namespace Penghou.Zhinu;

/// <summary>Configures retry and execution behavior for one durable step.</summary>
public sealed record StepOptions
{
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
