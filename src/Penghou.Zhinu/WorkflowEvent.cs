namespace Penghou.Zhinu;

/// <summary>
/// Describes an append-only committed-transition event. State rows remain
/// authoritative; the event stream is a committed-transition journal, not a
/// replacement execution model. <see cref="Durability"/> classifies whether the
/// event is durable execution truth or advisory progress/diagnostics.
/// </summary>
public sealed record WorkflowEvent
{
    public required long Sequence { get; init; }

    public required Guid WorkflowRunId { get; init; }

    public string? StepKey { get; init; }

    public required string EventType { get; init; }

    public required DateTimeOffset Timestamp { get; init; }

    public int? Attempt { get; init; }

    public string? DataJson { get; init; }

    /// <summary>
    /// Whether this event is durable execution-transition truth or advisory
    /// progress/diagnostics, derived from <see cref="EventType"/> via
    /// <see cref="WorkflowEventTypes.Durability(string)"/>.
    /// </summary>
    public WorkflowEventDurability Durability => WorkflowEventTypes.Durability(EventType);
}
