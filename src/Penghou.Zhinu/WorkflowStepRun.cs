namespace Penghou.Zhinu;

/// <summary>Represents the current durable state of one logical workflow step.</summary>
public sealed record WorkflowStepRun
{
    public required Guid Id { get; init; }

    public required Guid WorkflowRunId { get; init; }

    public required string StepKey { get; init; }

    public required StepStatus Status { get; init; }

    public required int Attempt { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? StartedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    public DateTimeOffset? AvailableAt { get; init; }

    public string? InputJson { get; init; }

    public string? InputType { get; init; }

    public string? InputHash { get; init; }

    public string? OutputJson { get; init; }

    public string? OutputType { get; init; }

    public WorkflowError? Error { get; init; }

    public string? LeaseOwner { get; init; }

    public DateTimeOffset? LeaseExpiresAt { get; init; }
}
