namespace Penghou.Zhinu;

/// <summary>
/// Reads a run's durable event stream as bounded, cursor-based pages. Pages are a
/// stateless view over the same per-run <see cref="WorkflowEvent.Sequence"/>
/// ordering used by <see cref="IWorkflowReader.GetEventsAsync"/>; the caller
/// holds the cursor. This is independent of durable export acknowledgement.
/// </summary>
public interface IWorkflowEventPageReader
{
    /// <summary>
    /// Reads up to <paramref name="limit"/> events for a run whose
    /// <see cref="WorkflowEvent.Sequence"/> is strictly greater than
    /// <paramref name="afterSequence"/>, in ascending order, with cursor metadata.
    /// </summary>
    Task<WorkflowEventPage> GetEventPageAsync(
        Guid workflowRunId,
        long afterSequence = 0,
        int limit = 100,
        CancellationToken cancellationToken = default);
}
