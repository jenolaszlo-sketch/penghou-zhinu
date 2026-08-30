namespace Penghou.Zhinu;

/// <summary>
/// Optional store capability for retry-safe signal sends. Implementations must
/// atomically persist the signal inbox row, operation identity, and durable
/// event represented by the returned receipt.
/// </summary>
public interface IIdempotentWorkflowSignalRepository
{
    ValueTask<SignalSendReceipt> SendSignalIdempotentlyAsync(
        Guid workflowRunId,
        string signalName,
        string? dataJson,
        Guid signalId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);
}
