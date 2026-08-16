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

    public string? InputJson { get; init; }

    public string? InputType { get; init; }

    public string? OutputJson { get; init; }

    public string? OutputType { get; init; }

    public WorkflowError? Error { get; init; }

    public string? LeaseOwner { get; init; }

    public DateTimeOffset? LeaseExpiresAt { get; init; }
}
