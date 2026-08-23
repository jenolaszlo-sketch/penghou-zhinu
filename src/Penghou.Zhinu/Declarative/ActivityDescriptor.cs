namespace Penghou.Zhinu.Declarative;

/// <summary>A portable identity for an activity's input or output contract.</summary>
public sealed record ActivityContract
{
    public required string TypeId { get; init; }
}

/// <summary>Describes an activity's identity and its input/output contracts. Does not contain executable code.</summary>
public sealed record ActivityDescriptor
{
    public required ActivityReference Reference { get; init; }
    public required ActivityContract Input { get; init; }
    public required ActivityContract Output { get; init; }
}
