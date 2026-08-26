namespace Penghou.Zhinu;

/// <summary>
/// Identifies a class-based step implementation independently from the durable
/// step key recorded in workflow history.
/// </summary>
public readonly record struct StepImplementationKey
{
    public StepImplementationKey(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value ?? string.Empty;

    internal void Validate(string parameterName)
    {
        if (string.IsNullOrWhiteSpace(Value))
            throw new ArgumentException("Step implementation key must not be blank.", parameterName);
    }
}
