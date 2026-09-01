namespace Penghou.Zhinu;

/// <summary>
/// Raised when a durable loop would exceed the host's configured lexical
/// nesting limit.
/// </summary>
public sealed class LoopNestingLimitExceededException : ZhinuException
{
    public LoopNestingLimitExceededException(
        string loopPath,
        int depth,
        int maximumDepth)
        : base($"Workflow loop '{loopPath}' has nesting depth {depth}, exceeding the configured maximum of {maximumDepth}.")
    {
        LoopPath = loopPath;
        Depth = depth;
        MaximumDepth = maximumDepth;
    }

    public string LoopPath { get; }

    public int Depth { get; }

    public int MaximumDepth { get; }
}
