namespace Penghou.Zhinu;

/// <summary>Provides stable identifiers for downstream idempotency.</summary>
public sealed record WorkflowStepContext(
    Guid WorkflowRunId,
    Guid StepExecutionId,
    string StepKey,
    int Attempt,
    int Revision,
    bool IsCompensation = false)
{
    /// <summary>
    /// The stable idempotency key of this step execution revision:
    /// <c>&lt;run&gt;:&lt;step&gt;:&lt;revision&gt;</c>, or
    /// <c>&lt;run&gt;:&lt;step&gt;:&lt;revision&gt;:compensation</c> for a
    /// compensation execution. It is unchanged across retries of the same
    /// revision, so downstream calls can deduplicate, and changes when a
    /// restart creates a new revision.
    /// </summary>
    public string IdempotencyKey =>
        IsCompensation
            ? $"{WorkflowRunId:D}:{StepKey}:{Revision}:compensation"
            : $"{WorkflowRunId:D}:{StepKey}:{Revision}";
}
