namespace Penghou.Zhinu;

/// <summary>Describes the result of atomically attempting to claim a step.</summary>
public enum StepClaimDisposition
{
    Acquired,
    Reused,
    Waiting,
    Busy,
    Failed,
    Cancelled,
    /// <summary>
    /// The run's generation is quiescing: it owns forward progression but
    /// schedules no new work. Poll again later; resumption reactivates it.
    /// </summary>
    Deferred,
    /// <summary>
    /// The run's generation no longer owns forward progression (superseded).
    /// Stop scheduling on this run; a newer generation owns it.
    /// </summary>
    Superseded
}
