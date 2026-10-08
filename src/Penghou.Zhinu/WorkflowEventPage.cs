namespace Penghou.Zhinu;

/// <summary>
/// One bounded, ordered page of a run's durable event stream with cursor
/// metadata. The read is stateless: the caller holds the cursor and passes it
/// back. It is independent of durable export acknowledgement
/// (<see cref="IWorkflowEventExportRepository"/>); the two share ordering but not
/// state.
/// <para>
/// This does not provide the snapshot-at-watermark handshake and does not imply
/// snapshot atomicity. <see cref="ThroughDurableSequence"/> is the highest
/// durable event sequence known from the event stream, not a projection
/// watermark.
/// </para>
/// </summary>
public sealed record WorkflowEventPage
{
    /// <summary>The returned events in ascending <see cref="WorkflowEvent.Sequence"/> order.</summary>
    public required IReadOnlyList<WorkflowEvent> Events { get; init; }

    /// <summary>
    /// The last sequence consumed by this page: the final returned event's
    /// sequence, or the supplied cursor when the page is empty. Monotonic; pass it
    /// back as the next <c>afterSequence</c>.
    /// </summary>
    public required long NextCursor { get; init; }

    /// <summary>
    /// Whether more events exist for the run beyond those returned. Explicit so
    /// callers never infer it from <c>Events.Count == limit</c>.
    /// </summary>
    public required bool HasMore { get; init; }

    /// <summary>
    /// The highest sequence at or before <see cref="NextCursor"/> that is a
    /// <see cref="WorkflowEventDurability.Durable"/> event. Advisory events never
    /// advance it. Distinct from <see cref="NextCursor"/>.
    /// </summary>
    public required long ThroughDurableSequence { get; init; }

    /// <summary>
    /// The earliest cursor from which incremental continuation remains valid.
    /// Zhinu does not truncate events within a run, so this is 0 today.
    /// </summary>
    public required long RetentionFloor { get; init; }

    /// <summary>
    /// Whether the supplied cursor can no longer be safely continued because
    /// required history is unavailable. Zhinu has no intra-run truncation, so this
    /// is always false today. It is never set for empty pages, no-new-events, or
    /// advisory-only tails.
    /// </summary>
    public required bool ResyncRequired { get; init; }
}
