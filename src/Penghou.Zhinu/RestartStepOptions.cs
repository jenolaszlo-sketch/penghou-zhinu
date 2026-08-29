namespace Penghou.Zhinu;

/// <summary>Options for restarting a durable step.</summary>
public sealed class RestartStepOptions
{
    /// <summary>
    /// Stable identity for a retry-safe restart. When supplied, the provider
    /// must either return the originally committed receipt or reject conflicting
    /// reuse; it must never apply the restart twice.
    /// </summary>
    public Guid? OperationId { get; init; }

    /// <summary>Which steps to invalidate. Defaults to <see cref="StepRestartMode.Dependents"/>.</summary>
    public StepRestartMode Mode { get; init; } = StepRestartMode.Dependents;

    /// <summary>Who initiated the restart, for auditability.</summary>
    public string? Actor { get; init; }

    /// <summary>Why the restart was requested, for auditability.</summary>
    public string? Reason { get; init; }
}
