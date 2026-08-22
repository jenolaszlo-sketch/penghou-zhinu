namespace Penghou.Zhinu;

/// <summary>Controls how a child workflow is started.</summary>
public sealed record ChildRunOptions
{
    /// <summary>
    /// Explicit child deadline. The effective child deadline is the earlier of
    /// this value and the parent run's deadline, so a child can never outlive
    /// the parent's hard execution deadline unless Zhinu explicitly supports
    /// detached children (it does not yet). Null means "no explicit override";
    /// a null parent deadline is also unbounded.
    /// </summary>
    public DateTimeOffset? Deadline { get; init; }

    /// <summary>Explicit metadata attached to the child run. Overrides inherited metadata when provided.</summary>
    public object? Metadata { get; init; }

    /// <summary>
    /// Copies the parent's metadata verbatim to the child when the child has no
    /// explicit <see cref="Metadata"/>. Defaults to <c>false</c>: metadata often
    /// contains tenant, security context, or owner fields that should not be
    /// inherited blindly.
    /// </summary>
    public bool InheritMetadata { get; init; }
}
