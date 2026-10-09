namespace Penghou.Zhinu;

/// <summary>
/// Optional store capability for reading an authoritative, watermarked run
/// projection snapshot from one consistent read boundary. The watermark and
/// all rows must come from the same storage snapshot; implementations must not
/// compose independently read parts under a separately read sequence number.
/// </summary>
public interface IWorkflowSnapshotRepository
{
    /// <summary>
    /// Reads the run projection snapshot with its durable watermark. Returns
    /// null when the run does not exist. Diagnosis fields are left unset; the
    /// reader host derives them from the same boundary's rows.
    /// </summary>
    ValueTask<WorkflowRunSnapshot?> ReadRunSnapshotAsync(
        Guid workflowRunId,
        RunSnapshotOptions options,
        CancellationToken cancellationToken = default);
}
