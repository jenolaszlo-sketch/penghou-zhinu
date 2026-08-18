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

    /// <summary>
    /// The run's <see cref="WorkflowRun.LeaseGeneration"/> observed when the
    /// worker claimed the run. Claiming a step requires it to match the run's
    /// current generation; a worker whose run was restarted is fenced out.
    /// </summary>
    public long LeaseGeneration { get; init; } = 1;

    /// <summary>Step keys this step durably depends on.</summary>
    public IReadOnlyCollection<string>? DependsOn { get; init; }
}
