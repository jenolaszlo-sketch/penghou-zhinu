namespace Penghou.Zhinu.Agents;

/// <summary>How an agent step treats existing checkpoints on (re)execution.</summary>
public enum AgentRestartMode
{
    /// <summary>Resume the latest checkpoint of the step session, including across step restarts.</summary>
    Resume = 0,
    /// <summary>Start a revision-scoped session; restarted steps never reuse abandoned-session outputs.</summary>
    Fresh = 1
}

/// <summary>Restart policy for one agent step.</summary>
public sealed record AgentStepOptions
{
    public AgentRestartMode RestartMode { get; init; } = AgentRestartMode.Resume;
}
