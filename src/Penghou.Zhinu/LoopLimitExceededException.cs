namespace Penghou.Zhinu;

/// <summary>
/// Raised when a durable loop exceeds its configured iteration count,
/// absolute deadline, or relative wall-clock budget.
/// </summary>
public sealed class LoopLimitExceededException : ZhinuException
{
    public LoopLimitExceededException(string loopKey, int maxIterations)
        : base($"Workflow loop '{loopKey}' exceeded its limit of {maxIterations} iterations.")
    {
        LoopKey = loopKey;
        MaxIterations = maxIterations;
        LimitKind = LoopLimitKind.IterationCount;
    }

    public LoopLimitExceededException(
        string loopKey,
        LoopLimitKind limitKind,
        DateTimeOffset deadline,
        TimeSpan? timeBudget = null)
        : base(CreateMessage(loopKey, limitKind, deadline, timeBudget))
    {
        if (limitKind is not (LoopLimitKind.Deadline or LoopLimitKind.TimeBudget))
            throw new ArgumentOutOfRangeException(nameof(limitKind));
        if (limitKind == LoopLimitKind.TimeBudget && timeBudget is null)
            throw new ArgumentNullException(nameof(timeBudget));
        if (limitKind == LoopLimitKind.Deadline && timeBudget is not null)
            throw new ArgumentException(
                "A deadline limit cannot carry a relative time budget.",
                nameof(timeBudget));

        LoopKey = loopKey;
        LimitKind = limitKind;
        Deadline = deadline;
        TimeBudget = timeBudget;
    }

    public string LoopKey { get; }

    public LoopLimitKind LimitKind { get; }

    public int? MaxIterations { get; }

    public DateTimeOffset? Deadline { get; }

    public TimeSpan? TimeBudget { get; }

    private static string CreateMessage(
        string loopKey,
        LoopLimitKind limitKind,
        DateTimeOffset deadline,
        TimeSpan? timeBudget) => limitKind switch
        {
            LoopLimitKind.Deadline =>
                $"Workflow loop '{loopKey}' exceeded its deadline of {deadline:O}.",
            LoopLimitKind.TimeBudget when timeBudget is { } budget =>
                $"Workflow loop '{loopKey}' exceeded its time budget of {budget} (deadline {deadline:O}).",
            LoopLimitKind.TimeBudget => throw new ArgumentNullException(nameof(timeBudget)),
            _ => throw new ArgumentOutOfRangeException(nameof(limitKind))
        };
}

/// <summary>Identifies which configured durable loop safety limit was reached.</summary>
public enum LoopLimitKind
{
    IterationCount,
    Deadline,
    TimeBudget
}
