namespace Penghou.Zhinu;

/// <summary>Describes an append-only diagnostic event emitted by a state transition.</summary>
public sealed record WorkflowEvent
{
    public required long Sequence { get; init; }

    public required Guid WorkflowRunId { get; init; }

    public string? StepKey { get; init; }

    public required string EventType { get; init; }

    public required DateTimeOffset Timestamp { get; init; }

    public int? Attempt { get; init; }

    public string? DataJson { get; init; }
}
