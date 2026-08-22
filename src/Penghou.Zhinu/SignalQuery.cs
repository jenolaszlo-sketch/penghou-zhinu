namespace Penghou.Zhinu;

/// <summary>Lifecycle of a buffered external signal record.</summary>
public enum SignalStatus
{
    /// <summary>Buffered in the run's inbox; not yet delivered to a waiting step.</summary>
    Buffered,

    /// <summary>Delivered to a waiting step. The delivered payload remains in the durable event history.</summary>
    Consumed
}

/// <summary>A readable signal record from a run's inbox.</summary>
/// <remarks>
/// Consumed signals are retained as durable <c>signal-delivered</c> events in the
/// workflow history; the <see cref="WorkflowSignalRecord"/> row is the inbox view
/// and can be purged without losing audit events.
/// </remarks>
public sealed record WorkflowSignalRecord
{
    public required Guid Id { get; init; }
    public required Guid WorkflowRunId { get; init; }
    public required string SignalName { get; init; }
    public string? DataJson { get; init; }
    public Guid? DeliveredStepId { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }

    public SignalStatus Status =>
        DeliveredStepId is null ? SignalStatus.Buffered : SignalStatus.Consumed;
}

/// <summary>Filters and pages signal records for one run.</summary>
public sealed record SignalQuery
{
    public string? SignalName { get; init; }
    public SignalStatus? Status { get; init; }
    /// <summary>Cursor: results strictly after this signal id in stable order (created_at, id).</summary>
    public Guid? AfterId { get; init; }
    public int Limit { get; init; } = 100;

    public void Validate()
    {
        if (SignalName is not null)
            ArgumentException.ThrowIfNullOrWhiteSpace(SignalName);
        if (Limit is < 1 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(Limit));
        if (AfterId is not null && AfterId.Value == Guid.Empty)
            throw new ArgumentOutOfRangeException(nameof(AfterId));
    }
}

/// <summary>Controls which signal records are removed from a run's inbox.</summary>
/// <remarks>
/// Purge removes inbox rows only; delivered signals remain in the durable
/// <c>signal-delivered</c> event history. Defaults to purging consumed signals
/// with no age bound, so callers must supply <see cref="OlderThan"/> or rely on
/// the default consumed-only scope to keep buffered signals intact.
/// </remarks>
public sealed record SignalPurgeOptions
{
    /// <summary>Only remove signals created strictly before this time. Null means no age bound.</summary>
    public DateTimeOffset? OlderThan { get; init; }

    /// <summary>Which lifecycle status to purge. Defaults to <see cref="SignalStatus.Consumed"/>.</summary>
    public SignalStatus? Status { get; init; } = SignalStatus.Consumed;

    /// <summary>Maximum rows to remove; null removes all matching rows.</summary>
    public int? Limit { get; init; }

    public void Validate()
    {
        if (Limit is < 0)
            throw new ArgumentOutOfRangeException(nameof(Limit));
    }
}
