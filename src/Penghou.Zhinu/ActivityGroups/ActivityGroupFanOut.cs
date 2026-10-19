using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Penghou.Zhinu;

/// <summary>
/// Declares per-item grouping at an existing durable selection boundary.
/// Changing selected membership or inputs requires a distinct stable native fan-out scope and
/// selection step. Replaying an admitted scope retains its immutable plan;
/// a new epoch never reinterprets historical item membership.
/// </summary>
public sealed class ActivityGroupFanOutBinding
{
    public ActivityGroupFanOutBinding(ActivityGroupMembership membership, string selectionStepKey,
        ActivityGroupOccurrence? parent = null)
    {
        Membership = membership ?? throw new ArgumentNullException(nameof(membership));
        SelectionStepKey = ActivityGroupText.Required(selectionStepKey, 1024, nameof(selectionStepKey));
        Parent = parent;
    }

    public ActivityGroupMembership Membership { get; }
    public string SelectionStepKey { get; }
    public ActivityGroupOccurrence? Parent { get; }
}

public enum ActivityGroupFanOutIdentityKind { Keyed = 0, Positional = 1 }

/// <summary>A bounded immutable selected item; contains hashes and identities, never argument payloads.</summary>
public sealed record ActivityGroupFanOutItem
{
    public required string ItemKey { get; init; }
    public required string StepKey { get; init; }
    public required string InputHash { get; init; }
    public required ActivityGroupStepBinding Group { get; init; }
}

/// <summary>The complete immutable enumeration admitted before any grouped item is dispatched.</summary>
public sealed class ActivityGroupFanOutPlan
{
    public const int MaximumAllowedItems = 4096;
    public const int MaximumAllowedCanonicalBytes = 1_048_576;

    private ActivityGroupFanOutPlan(Guid runId, string manifestDigest, string fanOutKey,
        ActivityGroupFanOutBinding binding, ActivityGroupFanOutIdentityKind kind,
        string inputType, string outputType, string? implementationKey, string? declarationHash,
        string compensationContract, IReadOnlyList<string> dependencies, IReadOnlyList<ActivityGroupFanOutItem> items,
        string? bodyContract)
    {
        WorkflowRunId = runId;
        GroupManifestDigest = manifestDigest;
        FanOutKey = fanOutKey;
        Binding = binding;
        IdentityKind = kind;
        InputType = inputType;
        OutputType = outputType;
        ImplementationKey = implementationKey;
        AuthorizationDeclarationHash = declarationHash;
        CompensationContract = compensationContract;
        Dependencies = dependencies;
        Items = items;
        BodyContract = bodyContract;
        CanonicalJson = Encode();
        if (Encoding.UTF8.GetByteCount(CanonicalJson) > MaximumAllowedCanonicalBytes)
            throw new WorkflowConfigurationException("Grouped fan-out enumeration exceeds its byte limit.");
        PlanDigest = Hash((bodyContract is null
            ? "penghou.zhinu.activity-group-fanout-plan.v1\n"
            : "penghou.zhinu.activity-group-fanout-plan.v2\n") + CanonicalJson);
    }

    public Guid WorkflowRunId { get; }
    public string GroupManifestDigest { get; }
    public string FanOutKey { get; }
    public ActivityGroupFanOutBinding Binding { get; }
    public ActivityGroupFanOutIdentityKind IdentityKind { get; }
    public string InputType { get; }
    public string OutputType { get; }
    public string? ImplementationKey { get; }
    public string? AuthorizationDeclarationHash { get; }
    public string CompensationContract { get; }
    public IReadOnlyList<string> Dependencies { get; }
    public IReadOnlyList<ActivityGroupFanOutItem> Items { get; }
    /// <summary>Stable body fingerprint for a per-item multi-step fan-out expansion.</summary>
    public string? BodyContract { get; }
    public string CanonicalJson { get; }
    public string PlanDigest { get; }

    internal static ActivityGroupFanOutPlan Create(Guid runId, ActivityGroupManifest manifest, string fanOutKey,
        ActivityGroupFanOutBinding binding, ActivityGroupFanOutIdentityKind kind,
        string inputType, string outputType, string? implementationKey, string? declarationHash,
        string compensationContract, IEnumerable<string> dependencies, IEnumerable<(string Key, string Hash)> items,
        string? bodyContract = null)
    {
        ActivityGroupText.Required(fanOutKey, 512, nameof(fanOutKey));
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (bodyContract is not null)
        {
            ActivityGroupText.Required(bodyContract, 512, nameof(bodyContract));
            if (binding.Membership.Role != ActivityGroupMemberRole.Control)
                throw new WorkflowConfigurationException("A grouped fan-out body requires Control membership for its item completion step.");
        }
        var dependencyArray = dependencies.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (dependencyArray.Length > MaximumAllowedItems || !dependencyArray.Contains(binding.SelectionStepKey, StringComparer.Ordinal))
            throw new WorkflowConfigurationException("Grouped fan-out must declare its existing selection step as a dependency.");
        foreach (var dependency in dependencyArray) ActivityGroupText.Required(dependency, 1024, nameof(dependencies));
        if (binding.Parent is not null && binding.Parent.ExecutionCoordinate != "[]")
            throw new WorkflowConfigurationException("This grouped fan-out profile supports root execution scope only.");
        manifest.ValidateMembership(binding.Membership);
        var definition = manifest.FindDefinition(binding.Membership.GroupKey)!;
        if (definition.ParentGroupKey != binding.Parent?.GroupKey ||
            (binding.Parent is not null && (binding.Parent.WorkflowRunId != runId || binding.Parent.ManifestDigest != manifest.ManifestDigest)))
            throw new WorkflowConfigurationException("Grouped fan-out requires the corresponding explicit parent from this run and manifest.");
        var selected = new List<ActivityGroupFanOutItem>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (key, hash) in items)
        {
            if (selected.Count == MaximumAllowedItems)
                throw new WorkflowConfigurationException("Grouped fan-out exceeds its item limit.");
            ActivityGroupText.Required(key, 512, nameof(items));
            if (!keys.Add(key)) throw new WorkflowConfigurationException("Grouped fan-out item keys must be unique.");
            var occurrence = ActivityGroupOccurrence.Create(runId, manifest, binding.Membership.GroupKey, binding.Parent,
                ActivityGroupOccurrence.FanOutCoordinate(fanOutKey, kind, key));
            selected.Add(new ActivityGroupFanOutItem
            {
                ItemKey = key,
                StepKey = fanOutKey + "." + key,
                InputHash = hash,
                Group = new ActivityGroupStepBinding(binding.Membership, occurrence)
            });
        }
        selected.Sort((left, right) => StringComparer.Ordinal.Compare(left.ItemKey, right.ItemKey));
        if (selected.Any(item => dependencyArray.Contains(item.StepKey, StringComparer.Ordinal)))
            throw new WorkflowConfigurationException("A grouped fan-out item cannot depend on a selected sibling or itself.");
        return new ActivityGroupFanOutPlan(runId, manifest.ManifestDigest, fanOutKey, binding, kind,
            inputType, outputType, implementationKey, declarationHash, compensationContract,
            Array.AsReadOnly(dependencyArray), new ReadOnlyCollection<ActivityGroupFanOutItem>(selected), bodyContract);
    }

    /// <summary>Identifies the immutable enumeration attached to one exact selection-step revision.</summary>
    public string GetExpansionId(Guid boundaryStepId) =>
        Hash("penghou.zhinu.activity-group-fanout-expansion.v1\n" +
            WorkflowRunId.ToString("D") + "\n" + boundaryStepId.ToString("D") + "\n" +
            FanOutKey);

    private string Encode()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", BodyContract is null ? 1 : 2);
            WriteString(writer, "run", WorkflowRunId.ToString("D"));
            WriteString(writer, "manifest", GroupManifestDigest);
            WriteString(writer, "fanOut", FanOutKey);
            WriteString(writer, "selection", Binding.SelectionStepKey);
            WriteString(writer, "group", Binding.Membership.GroupKey);
            WriteString(writer, "parent", Binding.Parent?.GroupOccurrenceId);
            writer.WriteNumber("role", (int)Binding.Membership.Role);
            writer.WriteNumber("kind", (int)IdentityKind);
            WriteString(writer, "inputType", InputType);
            WriteString(writer, "outputType", OutputType);
            WriteString(writer, "implementation", ImplementationKey);
            WriteString(writer, "authorization", AuthorizationDeclarationHash);
            WriteString(writer, "compensation", CompensationContract);
            if (BodyContract is not null)
                WriteString(writer, "bodyContract", BodyContract);
            writer.WriteStartArray("dependencies");
            foreach (var dependency in Dependencies) ActivityGroupManifest.WriteStringValue(writer, dependency);
            writer.WriteEndArray();
            writer.WriteStartArray("items");
            foreach (var item in Items)
            {
                writer.WriteStartObject();
                WriteString(writer, "key", item.ItemKey);
                WriteString(writer, "step", item.StepKey);
                WriteString(writer, "input", item.InputHash);
                WriteString(writer, "occurrence", item.Group.Occurrence.GroupOccurrenceId);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    internal static string CompensationSignature(CompensationMetadata? compensation)
    {
        if (compensation is null) return "none";
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            WriteString(writer, "retry", compensation.RetryPolicyJson);
            if (compensation.ExecutionTimeout is { } timeout) writer.WriteNumber("timeoutTicks", timeout.Ticks);
            else writer.WriteNull("timeoutTicks");
            WriteString(writer, "authorization", compensation.AuthorizationDeclarationJson);
            writer.WriteEndObject();
        }
        return Hash("penghou.zhinu.activity-group-fanout-compensation.v1\n" + Encoding.UTF8.GetString(stream.ToArray()));
    }

    private static void WriteString(Utf8JsonWriter writer, string propertyName, string? value)
    {
        writer.WritePropertyName(propertyName);
        if (value is null) writer.WriteNullValue();
        else ActivityGroupManifest.WriteStringValue(writer, value);
    }
}

public sealed record ActivityGroupFanOutRequest
{
    public required ActivityGroupFanOutPlan Plan { get; init; }
    public required string OwnerId { get; init; }
    public required long LeaseGeneration { get; init; }
    public required DateTimeOffset Now { get; init; }
}

public sealed record ActivityGroupFanOutState
{
    public required Guid WorkflowRunId { get; init; }
    public required string ExpansionId { get; init; }
    public required string FanOutKey { get; init; }
    public required Guid BoundaryStepId { get; init; }
    public required int BoundaryRevision { get; init; }
    public required string PlanDigest { get; init; }
    public required int ItemCount { get; init; }
    public required bool IsComplete { get; init; }
}

public sealed record ActivityGroupFanOutItemClaim
{
    public required string ExpansionId { get; init; }
    public required string ItemKey { get; init; }
    /// <summary>Non-null only for an independently durable operation inside a fan-out body.</summary>
    public string? BodyOperationKey { get; init; }
    /// <summary>Marks the reserved per-item completion claim, separate from body operations.</summary>
    public bool IsBodyCompletion { get; init; }
}

/// <summary>Optional dynamic-group capability. Existing group stores remain valid for the foundation profile.</summary>
public interface IWorkflowGroupFanOutRepository
{
    ValueTask<ActivityGroupFanOutState> AdmitGroupFanOutAsync(ActivityGroupFanOutRequest request, CancellationToken cancellationToken = default);
    ValueTask<ActivityGroupFanOutState> CompleteGroupFanOutAsync(ActivityGroupFanOutRequest request, CancellationToken cancellationToken = default);
    ValueTask<ActivityGroupFanOutState?> GetGroupFanOutAsync(Guid workflowRunId, string fanOutKey, CancellationToken cancellationToken = default);
    ValueTask<bool> HasIncompleteGroupFanOutAsync(Guid workflowRunId, CancellationToken cancellationToken = default);
}

/// <summary>Marker capability for stores that atomically validate bounded multi-step fan-out body claims.</summary>
public interface IWorkflowGroupFanOutBodyRepository : IWorkflowGroupFanOutRepository
{
}
