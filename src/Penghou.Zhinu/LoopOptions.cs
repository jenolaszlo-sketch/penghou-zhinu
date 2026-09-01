namespace Penghou.Zhinu;

/// <summary>Defines the host-enforced safety limit for a durable state loop.</summary>
public sealed class LoopOptions
{
    public LoopOptions(int maxIterations)
    {
        if (maxIterations <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxIterations));
        MaxIterations = maxIterations;
    }

    /// <summary>The maximum number of loop-body executions.</summary>
    public int MaxIterations { get; }
}
