using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Penghou.Zhinu;

/// <summary>A deterministic run-local realization of one group definition.</summary>
public sealed class ActivityGroupOccurrence
{
    public const int MaximumAllowedOccurrencesPerRun = 65_536;
    private const int MaximumCoordinateBytes = 32_768;

    private ActivityGroupOccurrence(Guid workflowRunId, string manifestDigest, string groupKey,
        ActivityGroupOccurrence? parent, string executionCoordinate)
    {
        WorkflowRunId = workflowRunId;
        ManifestDigest = manifestDigest;
        GroupKey = groupKey;
        Parent = parent;
        ExecutionCoordinate = executionCoordinate;
        GroupOccurrenceId = ComputeId(workflowRunId, groupKey, parent?.GroupOccurrenceId, executionCoordinate);
    }

    public Guid WorkflowRunId { get; }
    public string GroupOccurrenceId { get; }
    public string GroupKey { get; }
    public string? ParentGroupOccurrenceId => Parent?.GroupOccurrenceId;
    /// <summary>Canonical execution coordinates: v1 root/loop arrays, or a versioned fan-out item envelope.</summary>
    public string ExecutionCoordinate { get; }
    internal string ManifestDigest { get; }
    internal ActivityGroupOccurrence? Parent { get; }

    internal static ActivityGroupOccurrence Create(Guid runId, ActivityGroupManifest manifest, string groupKey,
        ActivityGroupOccurrence? parent, string coordinate)
    {
        if (runId == Guid.Empty)
            throw new ArgumentException("A group occurrence requires a run identity.", nameof(runId));
        var definition = manifest.Definitions.FirstOrDefault(item => string.Equals(item.GroupKey, groupKey, StringComparison.Ordinal))
            ?? throw new WorkflowConfigurationException("The group definition is not present in the admitted manifest.");
        if (!string.Equals(definition.ParentGroupKey, parent?.GroupKey, StringComparison.Ordinal))
            throw new WorkflowConfigurationException("A nested group requires its corresponding explicit parent occurrence.");
        if (parent is not null && (parent.WorkflowRunId != runId || parent.ManifestDigest != manifest.ManifestDigest))
            throw new WorkflowConfigurationException("A parent occurrence belongs to a different run or manifest.");
        var frames = ReadCoordinate(coordinate);
        if (parent is not null)
        {
            var parentFrames = ReadCoordinate(parent.ExecutionCoordinate);
            if (parentFrames.Count > frames.Count || !parentFrames.SequenceEqual(frames.Take(parentFrames.Count)))
                throw new WorkflowConfigurationException("A group parent must belong to the same or an enclosing execution coordinate.");
        }
        return new ActivityGroupOccurrence(runId, manifest.ManifestDigest, definition.GroupKey, parent, coordinate);
    }

    internal static string Coordinate(DurableLoopIterationIdentity? iteration)
    {
        var frames = new List<(string Name, int Number)>();
        for (var current = iteration; current is { } value; current = value.Scope.ParentIteration)
        {
            if (frames.Count == ActivityGroupManifestLimits.MaximumAllowedDepth)
                throw new WorkflowConfigurationException("Execution-coordinate depth exceeds the group limit.");
            frames.Add((value.Scope.Name, value.Number));
        }
        frames.Reverse();
        return EncodeCoordinate(frames);
    }

    internal static string FanOutCoordinate(string fanOutKey, ActivityGroupFanOutIdentityKind kind, string itemKey)
    {
        ActivityGroupText.Required(fanOutKey, 512, nameof(fanOutKey));
        ActivityGroupText.Required(itemKey, 512, nameof(itemKey));
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (kind == ActivityGroupFanOutIdentityKind.Positional &&
            (!int.TryParse(itemKey, NumberStyles.None, CultureInfo.InvariantCulture, out var index) || index < 0 ||
             index.ToString(CultureInfo.InvariantCulture) != itemKey))
            throw new WorkflowConfigurationException("Positional fan-out coordinates require a canonical nonnegative index.");
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", 2);
            writer.WriteStartArray("frames");
            writer.WriteStartObject();
            writer.WritePropertyName("fanOut");
            ActivityGroupManifest.WriteStringValue(writer, fanOutKey);
            writer.WriteString("kind", kind == ActivityGroupFanOutIdentityKind.Keyed ? "key" : "index");
            writer.WritePropertyName("item");
            ActivityGroupManifest.WriteStringValue(writer, itemKey);
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    internal static IReadOnlyList<ActivityGroupCoordinateFrame> ReadCoordinate(string coordinate)
    {
        if (coordinate.Length > MaximumCoordinateBytes || Encoding.UTF8.GetByteCount(coordinate) > MaximumCoordinateBytes)
            throw new WorkflowConfigurationException("Execution coordinate exceeds its byte limit.");
        try
        {
            using var json = JsonDocument.Parse(coordinate, new JsonDocumentOptions { MaxDepth = 4 });
            if (json.RootElement.ValueKind == JsonValueKind.Object)
            {
                var root = json.RootElement;
                var version = root.GetProperty("version").GetInt32();
                var array = root.GetProperty("frames");
                if (version != 2 || array.GetArrayLength() != 1)
                    throw new WorkflowConfigurationException("Unsupported fan-out coordinate version or shape.");
                var frame = array[0];
                var scope = frame.GetProperty("fanOut").GetString()!;
                var token = frame.GetProperty("item").GetString()!;
                var kind = frame.GetProperty("kind").GetString() switch
                {
                    "key" => ActivityGroupFanOutIdentityKind.Keyed,
                    "index" => ActivityGroupFanOutIdentityKind.Positional,
                    _ => throw new WorkflowConfigurationException("Unknown fan-out coordinate kind.")
                };
                if (FanOutCoordinate(scope, kind, token) != coordinate)
                    throw new WorkflowConfigurationException("Fan-out coordinate is not canonical.");
                return [new ActivityGroupCoordinateFrame(kind == ActivityGroupFanOutIdentityKind.Keyed ? "key" : "index", scope, token)];
            }
            var frames = new List<(string Name, int Number)>();
            foreach (var element in json.RootElement.EnumerateArray())
            {
                if (frames.Count == ActivityGroupManifestLimits.MaximumAllowedDepth)
                    throw new WorkflowConfigurationException("Execution-coordinate depth exceeds the group limit.");
                var name = element.GetProperty("loop").GetString()!;
                DurableLoopScope.ValidateName(name, nameof(coordinate));
                var number = element.GetProperty("iteration").GetInt32();
                if (number < 1)
                    throw new WorkflowConfigurationException("Loop coordinates require a positive iteration.");
                frames.Add((name, number));
            }
            if (!string.Equals(EncodeCoordinate(frames), coordinate, StringComparison.Ordinal))
                throw new WorkflowConfigurationException("Execution coordinate is not canonical.");
            return frames.Select(frame => new ActivityGroupCoordinateFrame("loop", frame.Name,
                frame.Number.ToString(CultureInfo.InvariantCulture))).ToArray();
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException or FormatException or OverflowException)
        {
            throw new WorkflowConfigurationException("Execution coordinate is malformed.", error);
        }
    }

    private static string EncodeCoordinate(IEnumerable<(string Name, int Number)> frames) =>
        "[" + string.Join(',', frames.Select(frame =>
            "{\"loop\":\"" + frame.Name + "\",\"iteration\":" + frame.Number.ToString(CultureInfo.InvariantCulture) + "}")) + "]";

    internal static string ComputeId(Guid runId, string groupKey, string? parentId, string coordinate)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(coordinate.StartsWith('{')
            ? "penghou.zhinu.activity-group-occurrence.v2\n"u8
            : "penghou.zhinu.activity-group-occurrence.v1\n"u8);
        AppendField(hash, runId.ToString("D", CultureInfo.InvariantCulture));
        AppendField(hash, groupKey);
        AppendField(hash, parentId);
        AppendField(hash, coordinate);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void AppendField(IncrementalHash hash, string? value)
    {
        Span<byte> length = stackalloc byte[4];
        var bytes = value is null ? null : Encoding.UTF8.GetBytes(value);
        BinaryPrimitives.WriteUInt32BigEndian(length, bytes is null ? uint.MaxValue : (uint)bytes.Length);
        hash.AppendData(length);
        if (bytes is not null)
            hash.AppendData(bytes);
    }
}

internal readonly record struct ActivityGroupCoordinateFrame(string Kind, string Scope, string Token);

/// <summary>Binds optional static membership to one explicit runtime occurrence.</summary>
public sealed class ActivityGroupStepBinding
{
    public ActivityGroupStepBinding(ActivityGroupMembership membership, ActivityGroupOccurrence occurrence)
    {
        ArgumentNullException.ThrowIfNull(membership);
        ArgumentNullException.ThrowIfNull(occurrence);
        if (!string.Equals(membership.GroupKey, occurrence.GroupKey, StringComparison.Ordinal))
            throw new ArgumentException("Membership must name the selected occurrence's definition.", nameof(membership));
        Membership = membership;
        Occurrence = occurrence;
    }

    public ActivityGroupMembership Membership { get; }
    public ActivityGroupOccurrence Occurrence { get; }
}

/// <summary>A persisted association to an exact durable step revision, above retry attempts.</summary>
public sealed record ActivityGroupStepMembership
{
    public required Guid WorkflowRunId { get; init; }
    public required Guid StepId { get; init; }
    public required string StepKey { get; init; }
    public required int StepRevision { get; init; }
    public required string GroupOccurrenceId { get; init; }
    public required ActivityGroupMemberRole Role { get; init; }
}
