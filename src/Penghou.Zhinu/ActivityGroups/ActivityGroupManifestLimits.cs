namespace Penghou.Zhinu;

/// <summary>Host limits that may narrow the fixed group-manifest ceilings.</summary>
public sealed class ActivityGroupManifestLimits
{
    public const int MaximumAllowedGroups = 4096;
    public const int MaximumAllowedDepth = 128;
    public const int MaximumAllowedCanonicalBytes = 1_048_576;

    public ActivityGroupManifestLimits(
        int maximumGroups = MaximumAllowedGroups,
        int maximumDepth = MaximumAllowedDepth,
        int maximumCanonicalBytes = MaximumAllowedCanonicalBytes)
    {
        if (maximumGroups is < 1 or > MaximumAllowedGroups)
            throw new ArgumentOutOfRangeException(nameof(maximumGroups));
        if (maximumDepth is < 1 or > MaximumAllowedDepth)
            throw new ArgumentOutOfRangeException(nameof(maximumDepth));
        if (maximumCanonicalBytes is < 1 or > MaximumAllowedCanonicalBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumCanonicalBytes));
        MaximumGroups = maximumGroups;
        MaximumDepth = maximumDepth;
        MaximumCanonicalBytes = maximumCanonicalBytes;
    }

    public int MaximumGroups { get; }
    public int MaximumDepth { get; }
    public int MaximumCanonicalBytes { get; }
}
