namespace Penghou.Zhinu;

/// <summary>Controls deterministic retries for one durable step.</summary>
public sealed record RetryPolicy
{
    public int MaxAttempts { get; init; } = 1;

    public TimeSpan InitialDelay { get; init; } = TimeSpan.Zero;

    public double BackoffCoefficient { get; init; } = 2.0;

    public TimeSpan? MaximumDelay { get; init; }

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
    }

    internal TimeSpan DelayAfter(int failedAttempt)
    {
        var ticks = InitialDelay.Ticks *
            Math.Pow(BackoffCoefficient, Math.Max(0, failedAttempt - 1));
        var boundedTicks = Math.Min(ticks, TimeSpan.MaxValue.Ticks);
        var delay = TimeSpan.FromTicks((long)boundedTicks);
        return MaximumDelay is not null && delay > MaximumDelay
            ? MaximumDelay.Value
            : delay;
    }
}
