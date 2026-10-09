namespace Penghou.Zhinu;

/// <summary>
/// Reads an authoritative, watermarked run projection snapshot. The returned
/// <see cref="WorkflowRunSnapshot"/> rows and its
/// <see cref="WorkflowRunSnapshot.ThroughDurableSequence"/> represent one
/// consistent read boundary; consumers continue with event-page reads after the
/// watermark.
/// </summary>
public interface IWorkflowSnapshotReader
{
    /// <summary>
    /// Returns an authoritative snapshot whose state includes every durable
    /// execution transition through the returned watermark. Returns null when
    /// the run does not exist.
    /// </summary>
    Task<WorkflowRunSnapshot?> GetRunSnapshotAsync(
        Guid workflowRunId,
        RunSnapshotOptions? options = null,
        CancellationToken cancellationToken = default);
}
