namespace Penghou.Zhinu;

/// <summary>Provides stable identifiers for downstream idempotency.</summary>
public sealed record WorkflowStepContext
{
    private readonly Func<WorkflowArtifactDescriptor, CancellationToken,
        ValueTask<WorkflowArtifactReference>>? publishArtifact;

    public WorkflowStepContext(
        Guid workflowRunId,
        Guid stepExecutionId,
        string stepKey,
        int attempt,
        int revision,
        bool isCompensation = false)
    {
        WorkflowRunId = workflowRunId;
        StepExecutionId = stepExecutionId;
        StepKey = stepKey;
        Attempt = attempt;
        Revision = revision;
        IsCompensation = isCompensation;
    }

    internal WorkflowStepContext(
        Guid workflowRunId,
        Guid stepExecutionId,
        string stepKey,
        int attempt,
        int revision,
        bool isCompensation,
        Func<WorkflowArtifactDescriptor, CancellationToken,
            ValueTask<WorkflowArtifactReference>> publishArtifact)
        : this(workflowRunId, stepExecutionId, stepKey, attempt, revision, isCompensation) =>
        this.publishArtifact = publishArtifact;

    public Guid WorkflowRunId { get; }
    public Guid StepExecutionId { get; }
    public string StepKey { get; }
    public int Attempt { get; }
    public int Revision { get; }
    public bool IsCompensation { get; }

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

    /// <summary>
    /// Durably publishes a reference to an artifact produced by this step.
    /// The reference remains inspectable if the step or workflow later fails.
    /// </summary>
    public ValueTask<WorkflowArtifactReference> PublishArtifactAsync(
        WorkflowArtifactDescriptor artifact,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (publishArtifact is null)
        {
            throw new InvalidOperationException(
                "Artifact publication is unavailable on a manually created step context.");
        }
        return publishArtifact(artifact, cancellationToken);
    }
}
