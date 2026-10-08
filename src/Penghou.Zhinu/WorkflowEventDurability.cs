namespace Penghou.Zhinu;

/// <summary>
/// Classifies a committed workflow event's authority. Both durable and advisory
/// events are persisted; this classifies meaning, not storage. A durable event
/// records a committed execution transition that state rows reflect. An advisory
/// event is informational progress or diagnostics that never determines
/// execution state. State rows remain authoritative, and the event stream is a
/// committed-transition journal, not a replacement execution model.
/// </summary>
public enum WorkflowEventDurability
{
    /// <summary>Committed execution-transition evidence that state rows reflect.</summary>
    Durable = 0,

    /// <summary>Non-authoritative progress or diagnostic evidence; never execution truth.</summary>
    Advisory = 1
}
