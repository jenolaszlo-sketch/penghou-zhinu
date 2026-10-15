using System.Security.Cryptography;
using System.Text;
using System.Globalization;
using System.Text.Json;

namespace Penghou.Zhinu;

/// <summary>
/// A validated, bounded and immutable annotation forest. Creating a manifest
/// neither registers executable groups nor changes a workflow fingerprint.
/// </summary>
public sealed class ActivityGroupManifest
{
    public const int CurrentContractVersion = 1;
    private const string DigestDomain = "penghou.zhinu.activity-groups.manifest.v1\n";
    private readonly HashSet<string> groupKeys;

    private ActivityGroupManifest(ActivityGroupDefinition[] definitions, string canonicalJson, string manifestDigest)
    {
        Definitions = Array.AsReadOnly(definitions);
        CanonicalJson = canonicalJson;
        ManifestDigest = manifestDigest;
        groupKeys = definitions.Select(definition => definition.GroupKey).ToHashSet(StringComparer.Ordinal);
    }

    public int ContractVersion => CurrentContractVersion;
    public IReadOnlyList<ActivityGroupDefinition> Definitions { get; }
    public string CanonicalJson { get; }
    public string ManifestDigest { get; }

    /// <summary>
    /// Captures and validates a forest without recursion. Host limits may only
    /// narrow hard ceilings. Definitions are ordered by ordinal group key in the
    /// canonical representation; declared child ordinals remain presentation data.
    /// </summary>
    public static ActivityGroupManifest Create(
        IEnumerable<ActivityGroupDefinition> definitions,
        ActivityGroupManifestLimits? limits = null,
        int contractVersion = CurrentContractVersion)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        if (contractVersion != CurrentContractVersion)
            throw new ArgumentOutOfRangeException(nameof(contractVersion), "Unsupported group-manifest contract version.");
        limits ??= new ActivityGroupManifestLimits();
        if (definitions.TryGetNonEnumeratedCount(out var count) && count > limits.MaximumGroups)
            throw new ArgumentException("Group count exceeds the manifest limit.", nameof(definitions));
        var snapshot = new List<ActivityGroupDefinition>();
        foreach (var definition in definitions)
        {
            if (snapshot.Count == limits.MaximumGroups)
                throw new ArgumentException("Group count exceeds the manifest limit.", nameof(definitions));
            snapshot.Add(definition ?? throw new ArgumentException("Group definitions cannot contain null.", nameof(definitions)));
        }
        ValidateForest(snapshot, limits.MaximumDepth);
        var ordered = snapshot.OrderBy(definition => definition.GroupKey, StringComparer.Ordinal).ToArray();
        using var bytes = new LimitedManifestStream(limits.MaximumCanonicalBytes);
        using (var writer = new Utf8JsonWriter(bytes))
        {
            writer.WriteStartObject();
            writer.WriteNumber("contractVersion", CurrentContractVersion);
            writer.WriteStartArray("definitions");
            writer.Flush();
            foreach (var definition in ordered)
            {
                WriteDefinition(writer, definition);
                // Bound temporary writer storage to one already bounded definition.
                writer.Flush();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.Flush();
        }
        var content = bytes.GetBuffer().AsSpan(0, checked((int)bytes.Length));
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(DigestDomain));
        hash.AppendData(content);
        return new ActivityGroupManifest(ordered, Encoding.UTF8.GetString(content),
            Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }

    /// <summary>Validates optional direct membership without scheduling or persisting work.</summary>
    public void ValidateMembership(ActivityGroupMembership? membership)
    {
        if (membership is not null && !groupKeys.Contains(membership.GroupKey))
            throw new ArgumentException("Membership references an unknown group.", nameof(membership));
    }

    private static void ValidateForest(IReadOnlyList<ActivityGroupDefinition> definitions, int maximumDepth)
    {
        var byKey = new Dictionary<string, ActivityGroupDefinition>(StringComparer.Ordinal);
        var siblingOrdinals = new HashSet<(string? Parent, int Ordinal)>();
        foreach (var definition in definitions)
        {
            if (!byKey.TryAdd(definition.GroupKey, definition))
                throw new ArgumentException("Duplicate group key.", nameof(definitions));
            if (!siblingOrdinals.Add((definition.ParentGroupKey, definition.ChildOrdinal)))
                throw new ArgumentException("Duplicate group child ordinal within one parent.", nameof(definitions));
        }
        var children = new Dictionary<string, List<ActivityGroupDefinition>>(StringComparer.Ordinal);
        var pending = new Queue<(ActivityGroupDefinition Definition, int Depth)>();
        foreach (var definition in definitions)
        {
            if (definition.ParentGroupKey is not { } parent)
            {
                pending.Enqueue((definition, 1));
                continue;
            }
            if (!byKey.ContainsKey(parent))
                throw new ArgumentException("Group references an unknown parent.", nameof(definitions));
            if (!children.TryGetValue(parent, out var members))
                children.Add(parent, members = []);
            members.Add(definition);
        }
        var visited = 0;
        while (pending.TryDequeue(out var entry))
        {
            if (entry.Depth > maximumDepth)
                throw new ArgumentException("Group nesting exceeds the manifest limit.", nameof(definitions));
            visited++;
            if (children.TryGetValue(entry.Definition.GroupKey, out var members))
            {
                foreach (var member in members)
                    pending.Enqueue((member, entry.Depth + 1));
            }
        }
        if (visited != definitions.Count)
            throw new ArgumentException("Group hierarchy contains a cycle.", nameof(definitions));
    }

    private static void WriteDefinition(Utf8JsonWriter writer, ActivityGroupDefinition definition)
    {
        writer.WriteStartObject();
        WriteString(writer, "groupKey", definition.GroupKey);
        WriteString(writer, "parentGroupKey", definition.ParentGroupKey);
        WriteString(writer, "kind", definition.Kind == ActivityGroupKind.Logical ? "Logical" : "Macro");
        WriteString(writer, "displayName", definition.DisplayName);
        writer.WriteNumber("childOrdinal", definition.ChildOrdinal);
        writer.WritePropertyName("provenance");
        if (definition.Provenance is { } provenance)
        {
            writer.WriteStartObject();
            WriteString(writer, "macroName", provenance.MacroName);
            WriteString(writer, "macroDefinitionFingerprint", provenance.MacroDefinitionFingerprint);
            WriteString(writer, "invocationKey", provenance.InvocationKey);
            WriteString(writer, "macroVersion", provenance.MacroVersion);
            WriteString(writer, "sourceReference", provenance.SourceReference);
            writer.WriteEndObject();
        }
        else
        {
            writer.WriteNullValue();
        }
        writer.WriteEndObject();
    }

    private static void WriteString(Utf8JsonWriter writer, string propertyName, string? value)
    {
        writer.WritePropertyName(propertyName);
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }
        // Freeze escaping independently of the runtime's changing encoder blocklist.
        // Validated strings emit printable ASCII literally (except quote/backslash),
        // and every other UTF-16 code unit as uppercase \uXXXX, including surrogate pairs.
        var quoted = new StringBuilder(value.Length + 2).Append('"');
        foreach (var character in value)
        {
            if (character is '"' or '\\')
                quoted.Append('\\').Append(character);
            else if (character is >= ' ' and <= '~')
                quoted.Append(character);
            else
                quoted.Append("\\u").Append(((int)character).ToString("X4", CultureInfo.InvariantCulture));
        }
        quoted.Append('"');
        writer.WriteRawValue(quoted.ToString(), skipInputValidation: true);
    }

    private sealed class LimitedManifestStream(int maximumBytes) : MemoryStream(Math.Min(maximumBytes, 4096))
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            ValidateSize(count);
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            ValidateSize(buffer.Length);
            base.Write(buffer);
        }

        private void ValidateSize(int count)
        {
            if (Length + count > maximumBytes)
                throw new ArgumentException("Canonical group manifest exceeds the byte limit.", "definitions");
        }
    }
}
