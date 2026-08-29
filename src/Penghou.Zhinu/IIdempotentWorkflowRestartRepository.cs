namespace Penghou.Zhinu;

/// <summary>
/// Optional store capability for retry-safe administrative step restarts.
/// Implementations must atomically persist the restart, operation identity,
/// and durable event represented by the returned receipt.
/// </summary>
public interface IIdempotentWorkflowRestartRepository
{
    ValueTask<RestartReceipt> RestartStepIdempotentlyAsync(
        Guid workflowRunId,
        string stepKey,
        StepRestartMode mode,
        Guid operationId,
        string? actor,
        string? reason,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);
}
