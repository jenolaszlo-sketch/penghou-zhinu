using System.Diagnostics.CodeAnalysis;

namespace Penghou.Zhinu.Declarative;

/// <summary>Stable reference to an activity by name and version. Identity is ordinal and case-sensitive.</summary>
internal sealed record ActivityReference
{
    public required string Name { get; init; }
    public required string Version { get; init; }

    public ActivityReference() { }

    [SetsRequiredMembers]
    public ActivityReference(string name, string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        Name = name;
        Version = version;
    }

    public override string ToString() => $"{Name}@{Version}";
}
