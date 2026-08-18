namespace Penghou.Zhinu;

/// <summary>Options for restarting a durable step.</summary>
public sealed class RestartStepOptions
{
    /// <summary>Which steps to invalidate. Defaults to <see cref="StepRestartMode.Dependents"/>.</summary>
    public StepRestartMode Mode { get; init; } = StepRestartMode.Dependents;

    /// <summary>Who initiated the restart, for auditability.</summary>
    public string? Actor { get; init; }

    /// <summary>Why the restart was requested, for auditability.</summary>
    public string? Reason { get; init; }
}
