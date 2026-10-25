namespace Penghou.Zhinu;

/// <summary>Describes authoring provenance, without introducing execution behavior.</summary>
public enum ActivityGroupKind
{
    Logical = 0,
    Macro = 1
}

/// <summary>Determines progress counting only; control failures remain visible.</summary>
public enum ActivityGroupMemberRole
{
    Work = 0,
    Control = 1
}

/// <summary>Bounded, opaque macro provenance. Does not contain invocation arguments.</summary>
public sealed class ActivityGroupMacroProvenance
{
    public ActivityGroupMacroProvenance(
        string macroName,
        string macroDefinitionFingerprint,
        string invocationKey,
        string? macroVersion = null,
        string? sourceReference = null)
    {
        MacroName = ActivityGroupText.Required(macroName, 1024, nameof(macroName));
        MacroDefinitionFingerprint = ActivityGroupText.Required(
            macroDefinitionFingerprint, 1024, nameof(macroDefinitionFingerprint));
        InvocationKey = ActivityGroupText.Required(invocationKey, 512, nameof(invocationKey));
        MacroVersion = macroVersion is null ? null : ActivityGroupText.Required(macroVersion, 128, nameof(macroVersion));
        SourceReference = sourceReference is null ? null : ActivityGroupText.SourceReference(sourceReference);
    }

    public string MacroName { get; }
    public string MacroDefinitionFingerprint { get; }
    public string InvocationKey { get; }
    public string? MacroVersion { get; }
    public string? SourceReference { get; }
}

/// <summary>An immutable group definition, separate from any runtime occurrence.</summary>
public sealed class ActivityGroupDefinition
{
    public ActivityGroupDefinition(
        string groupKey,
        ActivityGroupKind kind,
        string displayName,
        string? parentGroupKey = null,
        int childOrdinal = 0,
        ActivityGroupMacroProvenance? provenance = null)
    {
        GroupKey = ActivityGroupText.Required(groupKey, 512, nameof(groupKey));
        ParentGroupKey = parentGroupKey is null ? null : ActivityGroupText.Required(parentGroupKey, 512, nameof(parentGroupKey));
        DisplayName = ActivityGroupText.Required(displayName, 1024, nameof(displayName));
        if (kind is not (ActivityGroupKind.Logical or ActivityGroupKind.Macro))
            throw new ArgumentOutOfRangeException(nameof(kind));
        if (childOrdinal < 0)
            throw new ArgumentOutOfRangeException(nameof(childOrdinal));
        if ((kind == ActivityGroupKind.Macro) != (provenance is not null))
            throw new ArgumentException("Only macro groups require macro provenance.", nameof(provenance));
        Kind = kind;
        ChildOrdinal = childOrdinal;
        Provenance = provenance;
    }

    public string GroupKey { get; }
    public string? ParentGroupKey { get; }
    public ActivityGroupKind Kind { get; }
    public string DisplayName { get; }
    public int ChildOrdinal { get; }
    public ActivityGroupMacroProvenance? Provenance { get; }
}

/// <summary>
/// Declares direct innermost group ownership. A null membership represents
/// ungrouped work; this value alone does not attach a step to the runtime.
/// </summary>
public sealed class ActivityGroupMembership
{
    public ActivityGroupMembership(string groupKey, ActivityGroupMemberRole role = ActivityGroupMemberRole.Work)
    {
        GroupKey = ActivityGroupText.Required(groupKey, 512, nameof(groupKey));
        if (role is not (ActivityGroupMemberRole.Work or ActivityGroupMemberRole.Control))
            throw new ArgumentOutOfRangeException(nameof(role));
        Role = role;
    }

    public string GroupKey { get; }
    public ActivityGroupMemberRole Role { get; }
}
