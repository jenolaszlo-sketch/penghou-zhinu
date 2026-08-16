namespace Penghou.Zhinu;

/// <summary>Contains the stable execution shape required to claim a step.</summary>
public sealed record StepClaimRequest
{
    public required Guid WorkflowRunId { get; init; }

    public required string StepKey { get; init; }

    public string? InputJson { get; init; }

    public string? InputType { get; init; }

    public string? InputHash { get; init; }

    public required string OutputType { get; init; }

    public required string OwnerId { get; init; }

    public required DateTimeOffset Now { get; init; }

    public required DateTimeOffset LeaseExpiresAt { get; init; }
}
