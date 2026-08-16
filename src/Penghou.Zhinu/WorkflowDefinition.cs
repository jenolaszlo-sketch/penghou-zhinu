namespace Penghou.Zhinu;

/// <summary>Identifies workflow code that can execute or resume a durable run.</summary>
public sealed record WorkflowDefinition
{
    public required string Name { get; init; }

    public required string Version { get; init; }
}
