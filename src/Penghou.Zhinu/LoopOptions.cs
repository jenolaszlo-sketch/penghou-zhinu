namespace Penghou.Zhinu;

/// <summary>Defines host-enforced safety limits for a durable state loop.</summary>
public sealed class LoopOptions
{
    private TimeSpan? timeBudget;

    public LoopOptions(int maxIterations)
    {
        if (maxIterations <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxIterations));
        MaxIterations = maxIterations;
    }

    /// <summary>The maximum number of loop-body executions.</summary>
    public int MaxIterations { get; }

    /// <summary>
    /// Optional absolute wall-clock boundary after which the loop may not
    /// begin or commit more work.
    /// </summary>
    public DateTimeOffset? Deadline { get; init; }

    /// <summary>
    /// Optional wall-clock budget measured once from the loop's first durable
    /// entry. The resolved absolute boundary is persisted and is not reset by
    /// worker restarts.
    /// </summary>
    public TimeSpan? TimeBudget
    {
        get => timeBudget;
        init
        {
            if (value is { } budget && budget <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(TimeBudget));
            timeBudget = value;
        }
    }
}
