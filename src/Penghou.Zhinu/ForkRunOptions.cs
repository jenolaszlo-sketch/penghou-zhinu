namespace Penghou.Zhinu;

/// <summary>Options for creating a new run from committed work in another run.</summary>
public sealed class ForkRunOptions
{
    /// <summary>Which source steps are invalidated from the selected boundary.</summary>
    public StepRestartMode Mode { get; init; } = StepRestartMode.Dependents;

    /// <summary>Optional identifier for the new run.</summary>
    public Guid? WorkflowRunId { get; init; }

    /// <summary>Optional deadline for the new run. Source deadlines are not inherited.</summary>
    public DateTimeOffset? Deadline { get; init; }

    /// <summary>Who initiated the fork, recorded in the durable fork event.</summary>
    public string? Actor { get; init; }

    /// <summary>Why the fork was initiated, recorded in the durable fork event.</summary>
    public string? Reason { get; init; }
}
