namespace Penghou.Zhinu;

/// <summary>Represents the current durable state of one workflow execution.</summary>
public sealed record WorkflowRun
{
    public required Guid Id { get; init; }

    public required string WorkflowName { get; init; }

    public required string WorkflowVersion { get; init; }

    public required WorkflowStatus Status { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>
    /// The absolute time after which the run is no longer eligible to execute.
    /// When a run is claimed after its deadline passes, the engine fails it with
    /// a timeout error instead of executing it. Null means no deadline.
    /// </summary>
    public DateTimeOffset? Deadline { get; init; }

    public string? InputJson { get; init; }

    public string? InputType { get; init; }

    public string? OutputJson { get; init; }

    public string? OutputType { get; init; }

    public WorkflowError? Error { get; init; }

    /// <summary>
    /// Optional caller-supplied metadata serialized as JSON (for example
    /// correlation ids, owners, or tags). Metadata does not participate in
    /// idempotency or workflow contract validation.
    /// </summary>
    public string? MetadataJson { get; init; }

    public string? LeaseOwner { get; init; }

    public DateTimeOffset? LeaseExpiresAt { get; init; }
}
