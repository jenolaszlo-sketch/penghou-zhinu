namespace Penghou.Zhinu.Declarative;

/// <summary>Validated, canonical, immutable executable definition produced by the compiler.</summary>
internal sealed record CompiledWorkflowDefinition
{
    public required string Name { get; init; }
    public required string Version { get; init; }
    public required string Fingerprint { get; init; }
    public required IReadOnlyList<CompiledWorkflowStep> Steps { get; init; }
}

internal sealed record CompiledWorkflowStep
{
    public required string Id { get; init; }
    public required ActivityReference Activity { get; init; }
    public required IReadOnlyList<string> DependsOn { get; init; }
    public required ActivityDescriptor Descriptor { get; init; }
}
