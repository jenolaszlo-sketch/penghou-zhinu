namespace Penghou.Zhinu.Declarative;

/// <summary>Declarative source model for a workflow. Contains no executable delegates.</summary>
internal sealed record DeclarativeWorkflowDefinition
{
    public required string Name { get; init; }
    public required string Version { get; init; }
    public required IReadOnlyList<DeclarativeWorkflowStep> Steps { get; init; }
}

/// <summary>One step in a declarative workflow. For the minimal vertical, steps are sequential.</summary>
internal sealed record DeclarativeWorkflowStep
{
    public required string Id { get; init; }
    public required ActivityReference Activity { get; init; }
    public IReadOnlyList<string> DependsOn { get; init; } = Array.Empty<string>();
}
