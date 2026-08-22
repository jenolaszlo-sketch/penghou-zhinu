namespace Penghou.Zhinu;

/// <summary>Typed signal descriptor binding a signal name to its payload type.</summary>
/// <typeparam name="TPayload">Payload type serialized as the signal data.</typeparam>
public sealed record SignalDefinition<TPayload>
{
    public string Name { get; }

    public SignalDefinition(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
    }
}
