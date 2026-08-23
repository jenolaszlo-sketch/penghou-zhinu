namespace Penghou.Zhinu.Declarative;

/// <summary>Contract for an activity's input or output. For the minimal vertical, CLR type is sufficient.</summary>
public sealed record ActivityContract
{
    public required Type ClrType { get; init; }
}

/// <summary>Describes an activity's identity and its input/output contracts. Does not contain executable code.</summary>
public sealed record ActivityDescriptor
{
    public required ActivityReference Reference { get; init; }
    public required ActivityContract Input { get; init; }
    public required ActivityContract Output { get; init; }
}
