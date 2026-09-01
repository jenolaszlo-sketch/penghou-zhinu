namespace Penghou.Zhinu;

/// <summary>
/// Raised when a durable loop still requests another iteration after its
/// configured maximum number of body executions has committed.
/// </summary>
public sealed class LoopLimitExceededException : ZhinuException
{
    public LoopLimitExceededException(string loopKey, int maxIterations)
        : base($"Workflow loop '{loopKey}' exceeded its limit of {maxIterations} iterations.")
    {
        LoopKey = loopKey;
        MaxIterations = maxIterations;
    }

    public string LoopKey { get; }

    public int MaxIterations { get; }
}
