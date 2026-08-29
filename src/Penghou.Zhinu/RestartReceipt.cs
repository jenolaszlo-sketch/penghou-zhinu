namespace Penghou.Zhinu;

/// <summary>
/// Authoritative receipt for a retry-safe administrative step restart.
/// The event and workflow changes represented here are committed atomically.
/// </summary>
public sealed record RestartReceipt
{
    public required Guid OperationId { get; init; }

    public required RestartPlan Plan { get; init; }

    public required StepRestartMode Mode { get; init; }

    public required long LeaseGeneration { get; init; }

    public required WorkflowEvent Event { get; init; }

    public string? Actor { get; init; }

    public string? Reason { get; init; }

    /// <summary>
    /// True only for the call that committed the restart. False means this is
    /// the receipt of an identical operation that had already committed.
    /// </summary>
    public required bool WasApplied { get; init; }
}
