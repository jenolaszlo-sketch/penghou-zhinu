namespace Penghou.Zhinu;

/// <summary>Describes the successful control decision produced by a loop body.</summary>
public enum LoopBodyOutcomeKind
{
    /// <summary>Commit the supplied state and evaluate the next iteration.</summary>
    Continue,

    /// <summary>Commit the supplied final state and complete the loop normally.</summary>
    Break
}
