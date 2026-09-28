namespace Penghou.Zhinu;

/// <summary>
/// Durable event export with at-least-once delivery. Events carry stable
/// identity (<c>WorkflowRunId</c> plus <c>Sequence</c>) so receivers
/// deduplicate replays. A crash after sending but before acknowledgement
/// replays safely from the persisted cursor. The exporter never becomes
/// execution authority; retention skips runs with lagging consumers.
/// </summary>
public interface IWorkflowEventExportRepository
{
    /// <summary>
    /// Reads up to <paramref name="limit"/> events after the consumer's
    /// persisted cursor, in sequence order. Limit is 1 to 1000.
    /// </summary>
    ValueTask<IReadOnlyList<WorkflowEvent>> ReadExportBatchAsync(
        string consumerId,
        Guid workflowRunId,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Advances the consumer cursor. Acknowledgements are idempotent and
    /// monotonic: only forward movement is recorded. Acknowledging a purged
    /// or unknown run is a no-op success. Reads alone never register: only an
    /// acknowledged-behind position blocks retention, so operators should
    /// check preview before purging during active exports.
    /// </summary>
    ValueTask AcknowledgeExportAsync(
        string consumerId,
        Guid workflowRunId,
        long sequence,
        CancellationToken cancellationToken = default);
}
