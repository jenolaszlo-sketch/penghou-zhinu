namespace Penghou.Zhinu;

/// <summary>Configures retry and execution behavior for one durable step.</summary>
public sealed record StepOptions
{
    public RetryPolicy Retry { get; init; } = new();

    public TimeSpan? ExecutionTimeout { get; init; }

    internal void Validate()
    {
        Retry.Validate();
        if (ExecutionTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(ExecutionTimeout));
    }
}
