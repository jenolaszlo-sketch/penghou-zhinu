namespace Penghou.Zhinu.Ir;

/// <summary>Minimal scaffold for the future declarative workflow IR (Phase 1).</summary>
/// <remarks>Current <c>WorkflowArtifact</c> refers to external file references; this IR artifact
/// will carry compiled workflow graphs. Name is prefixed to avoid collision until the IR is executed.</remarks>
internal sealed record WorkflowIrArtifact
{
    public required string Name { get; init; }
    public required string Version { get; init; }
    public required string ContentHash { get; init; }
    public required IReadOnlyList<WorkflowIrState> States { get; init; }
    public required IReadOnlyList<WorkflowIrTransition> Transitions { get; init; }
    public IReadOnlyList<ActivityReference> Activities { get; init; } = [];
}

internal sealed record WorkflowIrState
{
    public required string Name { get; init; }
    public string? Activity { get; init; }
    public bool IsTerminal { get; init; }
}

internal sealed record WorkflowIrTransition
{
    public required string From { get; init; }
    public required string To { get; init; }
    public string? Condition { get; init; }
}

internal sealed record ActivityReference
{
    public required string Name { get; init; }
    public required string Version { get; init; }
    public string? InputSchema { get; init; }
    public string? OutputSchema { get; init; }
    public IReadOnlyList<string> Capabilities { get; init; } = [];
}

internal sealed record WorkflowIrPolicyRequirement
{
    public required string Code { get; init; }
    public required string Description { get; init; }
}

internal sealed record WorkflowIrCapabilityRequirement
{
    public required string Capability { get; init; }
    public string? Scope { get; init; }
}
