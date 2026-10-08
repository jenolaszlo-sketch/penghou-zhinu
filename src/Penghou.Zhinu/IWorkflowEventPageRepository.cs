namespace Penghou.Zhinu;

/// <summary>
/// Optional store capability for reading a run's durable events as bounded,
/// cursor-based pages. Separate from <see cref="IWorkflowEventExportRepository"/>:
/// page reads are stateless and client-cursored, while export carries durable
/// per-consumer acknowledgement.
/// </summary>
public interface IWorkflowEventPageRepository
{
    /// <summary>
    /// Reads one page of the run's events whose sequence is strictly greater than
    /// <paramref name="afterSequence"/>, in ascending order.
    /// </summary>
    ValueTask<WorkflowEventPage> ReadEventPageAsync(
        Guid workflowRunId,
        long afterSequence,
        int limit,
        CancellationToken cancellationToken = default);
}
