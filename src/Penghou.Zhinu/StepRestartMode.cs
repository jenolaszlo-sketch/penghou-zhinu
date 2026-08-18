namespace Penghou.Zhinu;

/// <summary>Selects which steps a restart invalidates.</summary>
public enum StepRestartMode
{
    /// <summary>
    /// Invalidates the selected step and every transitive step that depends on
    /// it through the durable dependency graph. Unrelated branches keep their
    /// committed results. The default for new applications.
    /// </summary>
    Dependents,

    /// <summary>
    /// Invalidates only the selected step. Previously completed dependents keep
    /// their results, which may then contain stale data derived from the old
    /// revision; this is an advanced operation and requires explicit opt-in.
    /// </summary>
    StepOnly,

    /// <summary>
    /// Invalidates the selected step and every step created at or after it.
    /// Preserves the pre-0.2 creation-order behavior and is retained only for
    /// compatibility; considered legacy.
    /// </summary>
    CreationOrder
}
