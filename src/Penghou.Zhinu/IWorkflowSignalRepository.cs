namespace Penghou.Zhinu;

/// <summary>
/// Persists external signals and their delivery to waiting steps.
/// </summary>
public interface IWorkflowSignalRepository
{
    ValueTask SendSignalAsync(
        Guid workflowRunId,
        string signalName,
        string? dataJson,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Attempts to complete the signal wait represented by <paramref name="stepId"/>
    /// with the oldest buffered undelivered signal matching
    /// <paramref name="signalName"/>. Returns the delivered payload, or null when
    /// no signal is available yet (the step stays waiting). Freshly claimed
    /// steps are first transitioned to <see cref="StepStatus.Waiting"/>.
    /// </summary>
    ValueTask<SignalDelivery?> TryDeliverSignalAsync(
        Guid stepId,
        string ownerId,
        string signalName,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);
}
