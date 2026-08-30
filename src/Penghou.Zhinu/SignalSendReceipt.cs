namespace Penghou.Zhinu;

/// <summary>
/// Authoritative receipt for a retry-safe signal send. The inbox row, durable
/// event, and operation identity represented here are committed atomically.
/// </summary>
public sealed record SignalSendReceipt
{
    public required Guid SignalId { get; init; }

    public required Guid WorkflowRunId { get; init; }

    public required string SignalName { get; init; }

    public required WorkflowEvent Event { get; init; }

    /// <summary>
    /// True only for the call that committed the signal. False means an
    /// identical signal had already committed under this identity.
    /// </summary>
    public required bool WasBuffered { get; init; }
}
