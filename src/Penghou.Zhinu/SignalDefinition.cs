namespace Penghou.Zhinu;

/// <summary>Typed signal descriptor binding a signal name to its payload type.</summary>
/// <typeparam name="TPayload">Payload type serialized as the signal data.</typeparam>
public sealed record SignalDefinition<TPayload>(string Name)
{
    public string Name { get; init; } =
        string.IsNullOrWhiteSpace(Name)
            ? throw new ArgumentException("Signal name must not be empty.", nameof(Name))
            : Name;
}
