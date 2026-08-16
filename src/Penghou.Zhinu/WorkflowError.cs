namespace Penghou.Zhinu;

/// <summary>Contains a stable, serializable representation of an execution failure.</summary>
public sealed record WorkflowError
{
    public required string Type { get; init; }

    public required string Message { get; init; }

    public string? StackTrace { get; init; }

    public required DateTimeOffset Timestamp { get; init; }

    public int? Attempt { get; init; }

    internal static WorkflowError FromException(
        Exception exception,
        DateTimeOffset timestamp,
        int? attempt = null) =>
        new()
        {
            Type = exception.GetType().FullName ?? exception.GetType().Name,
            Message = exception.Message,
            StackTrace = exception.StackTrace,
            Timestamp = timestamp,
            Attempt = attempt
        };
}
