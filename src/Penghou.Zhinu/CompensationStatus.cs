namespace Penghou.Zhinu;

/// <summary>
/// The lifecycle state of one registered compensation execution, persisted in
/// <c>workflow_step_compensations</c>. A compensation exists to undo a
/// committed forward step result; it is registered when the step is claimed,
/// awaits the committed result while <see cref="Pending"/>, and is later run
/// or explicitly skipped.
/// </summary>
public enum CompensationStatus
{
    /// <summary>Registered; waiting to run against the committed forward result.</summary>
    Pending = 0,

    /// <summary>Currently executing.</summary>
    Running = 1,

    /// <summary>Executed successfully.</summary>
    Completed = 2,

    /// <summary>Failed and will not be retried further without intervention.</summary>
    Failed = 3,

    /// <summary>Will not run: the forward step never committed, or the
    /// compensation was superseded.</summary>
    Skipped = 4
}
