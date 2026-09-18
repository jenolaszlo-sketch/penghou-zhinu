namespace Penghou.Zhinu;

/// <summary>Contains the stable execution shape required to claim a step.</summary>
public sealed record StepClaimRequest
{
    public required Guid WorkflowRunId { get; init; }

    public required string StepKey { get; init; }

    /// <summary>
    /// The class-based implementation selected for this invocation. Null for
    /// functional steps and built-in durable operations.
    /// </summary>
    public string? ImplementationKey { get; init; }

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

    /// <summary>
    /// Durable registration of the compensation that undoes this step's
    /// committed forward result. When set, the store records a compensation row
    /// (pending) as part of the claim, fills it with the committed result on
    /// completion, and marks it skipped on terminal forward failure.
    /// </summary>
    public CompensationMetadata? Compensation { get; init; }

    /// <summary>
    /// Allows a completed step whose input contract changed to be superseded by
    /// a fresh revision instead of failing the reuse contract. The engine sets
    /// this only for forked runs, where a step inherited from the source run
    /// may legitimately be re-run because the destination definition supplies
    /// different inputs (cross-version mutation). The previous revision is kept
    /// as history. Defaults to false, preserving strict durable reuse.
    /// </summary>
    public bool AllowSupersede { get; init; }
}
