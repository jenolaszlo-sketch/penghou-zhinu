namespace Penghou.Zhinu;

/// <summary>
/// Optional store capability for retaining the actor and reason supplied with
/// a durable cancellation in the cancellation event payload.
/// </summary>
public interface IAuditedWorkflowCancellationRepository
{
    ValueTask CancelRunAsync(
        Guid workflowRunId,
        string? actor,
        string? reason,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);
}
