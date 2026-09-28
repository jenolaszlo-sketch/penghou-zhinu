namespace Penghou.Zhinu;

/// <summary>Controls deterministic retries for one durable step.</summary>
public sealed record RetryPolicy
{
    public int MaxAttempts { get; init; } = 1;

    public TimeSpan InitialDelay { get; init; } = TimeSpan.Zero;

    public double BackoffCoefficient { get; init; } = 2.0;

    public TimeSpan? MaximumDelay { get; init; }

    /// <summary>
    /// Fraction of the computed delay applied as symmetric jitter, from 0
    /// (deterministic, the default) to 1. The jittered result is persisted as
    /// the step's eligible time, so replays observe the same bound.
    /// </summary>
    public double JitterFactor { get; init; }

    /// <summary>
    /// Exception type full names (including base types) that never retry.
    /// Null or empty retries every failure, preserving historical behavior.
    /// Type names are data and persist safely; delegates never do.
    /// </summary>
    public IReadOnlyList<string>? NonRetryableErrorTypes { get; init; }

    /// <summary>Whether a failure with this policy gets another attempt.</summary>
    public bool IsRetryable(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (NonRetryableErrorTypes is null or { Count: 0 })
            return true;
        for (var type = exception.GetType(); type is not null; type = type.BaseType)
        {
            if (NonRetryableErrorTypes.Contains(type.FullName, StringComparer.Ordinal))
                return false;
        }
        return true;
    }

    internal void Validate()
    {
        if (MaxAttempts < 1)
            throw new ArgumentOutOfRangeException(nameof(MaxAttempts));
        if (InitialDelay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(InitialDelay));
        if (BackoffCoefficient < 1 || double.IsNaN(BackoffCoefficient))
            throw new ArgumentOutOfRangeException(nameof(BackoffCoefficient));
        if (MaximumDelay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(MaximumDelay));
        if (JitterFactor is < 0 or > 1 || double.IsNaN(JitterFactor))
            throw new ArgumentOutOfRangeException(nameof(JitterFactor));
    }

    internal TimeSpan DelayAfter(int failedAttempt)
    {
        var ticks = InitialDelay.Ticks *
            Math.Pow(BackoffCoefficient, Math.Max(0, failedAttempt - 1));
        var boundedTicks = Math.Min(ticks, TimeSpan.MaxValue.Ticks);
        var delay = TimeSpan.FromTicks((long)boundedTicks);
        if (MaximumDelay is not null && delay > MaximumDelay)
            delay = MaximumDelay.Value;
        if (JitterFactor > 0)
        {
            var jitter = (Random.Shared.NextDouble() * 2 - 1) * JitterFactor;
            var jitteredTicks = (long)(delay.Ticks * (1 + jitter));
            delay = TimeSpan.FromTicks(Math.Max(0, jitteredTicks));
        }
        return delay;
    }
}
