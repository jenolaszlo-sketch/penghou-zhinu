namespace Penghou.Zhinu;

/// <summary>
/// The durable registration of one step compensation. A compensation undoes
/// the committed forward result of one step revision; it receives that result
/// (often the resource id needed to undo the operation). Rows are written
/// separately from <see cref="WorkflowStepRun"/>, one per
/// <c>(workflow_run_id, step_key, revision)</c>, so restart history and
/// compensation history stay independently understandable.
/// </summary>
public sealed record WorkflowStepCompensation
{
    public required Guid Id { get; init; }

    public required Guid WorkflowRunId { get; init; }

    /// <summary>The step whose committed forward result this compensates.</summary>
    public required string StepKey { get; init; }

    /// <summary>The step execution revision this compensation belongs to.</summary>
    public required int Revision { get; init; }

    /// <summary>
    /// Stable identity of the compensation kind, used to dispatch the
    /// compensating delegate when the workflow re-registers it. Defaults to the
    /// step key.
    /// </summary>
    public required string CompensationName { get; init; }

    public required CompensationStatus Status { get; init; }

    public required int Attempt { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>The committed forward result, serialized with the run's
    /// serializer. This is what the compensating delegate receives.</summary>
    public string? InputJson { get; init; }

    public string? InputType { get; init; }

    /// <summary>The compensation's own result once it has run.</summary>
    public string? OutputJson { get; init; }

    public WorkflowError? Error { get; init; }

    /// <summary>Serialized <see cref="RetryPolicy"/> for compensation attempts.</summary>
    public string? RetryPolicyJson { get; init; }

    /// <summary>Per-execution timeout for the compensating delegate.</summary>
    public TimeSpan? ExecutionTimeout { get; init; }

    public DateTimeOffset? StartedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>When a scheduled compensation attempt may run.</summary>
    public DateTimeOffset? AvailableAt { get; init; }

    /// <summary>Absolute deadline for the compensation attempt.</summary>
    public DateTimeOffset? TimeoutAt { get; init; }

    public string? LeaseOwner { get; init; }

    public DateTimeOffset? LeaseExpiresAt { get; init; }

    /// <summary>
    /// The run's <see cref="WorkflowRun.LeaseGeneration"/> at registration, so
    /// a compensation row written by a stale worker is fenced out after a
    /// restart.
    /// </summary>
    public long LeaseGeneration { get; init; } = 1;

    /// <summary>Who initiated the rollback this compensation belongs to.</summary>
    public string? Actor { get; init; }

    /// <summary>Why the rollback was requested, for auditability.</summary>
    public string? Reason { get; init; }

    /// <summary>Stable downstream idempotency key for the compensating call.</summary>
    public string? IdempotencyKey { get; init; }
}
