using FluentAssertions;
using System.Text.Json;

namespace Penghou.Zhinu.Tests;

public sealed class ActivityGroupFanOutContractTests
{
    private static readonly Guid RunId = Guid.Parse("90c31847-b3ea-4e4d-9a06-2a60bfb255bd");

    // Independently computed with PowerShell/.NET SHA256 over the documented
    // domain and four big-endian length-prefixed UTF-8 fields (null = FFFFFFFF).
    [Theory]
    [InlineData(ActivityGroupFanOutIdentityKind.Keyed, "a", "726b59ff0aba37aec4d6ae0fc26947a1e48246ec0c3405daca5b67bea1dde558")]
    [InlineData(ActivityGroupFanOutIdentityKind.Positional, "0", "041c8d299c2398f102fab829b3372d99c3b2324f39d9292ea1690baa26f932e0")]
    public void OccurrenceV2_MatchesIndependentGoldenVector(ActivityGroupFanOutIdentityKind kind, string key, string expected)
    {
        var coordinate = ActivityGroupOccurrence.FanOutCoordinate("items", kind, key);
        var occurrence = ActivityGroupOccurrence.Create(RunId, Manifest(nested: false), "work", null, coordinate);
        occurrence.GroupOccurrenceId.Should().Be(expected);
    }

    [Fact]
    public void FanOutCoordinate_IsCanonicalAndSeparatesPrefixKindAndItemKey()
    {
        var keyedA = ActivityGroupOccurrence.FanOutCoordinate("items", ActivityGroupFanOutIdentityKind.Keyed, "a");
        var keyedB = ActivityGroupOccurrence.FanOutCoordinate("items", ActivityGroupFanOutIdentityKind.Keyed, "b");
        var otherPrefix = ActivityGroupOccurrence.FanOutCoordinate("other", ActivityGroupFanOutIdentityKind.Keyed, "a");
        var positional = ActivityGroupOccurrence.FanOutCoordinate("items", ActivityGroupFanOutIdentityKind.Positional, "0");

        keyedA.Should().NotBe(keyedB);
        keyedA.Should().NotBe(otherPrefix);
        keyedA.Should().NotBe(positional);
        var frame = ActivityGroupOccurrence.ReadCoordinate(keyedA).Should().ContainSingle().Which;
        frame.Kind.Should().Be("key");
        frame.Scope.Should().Be("items");
        frame.Token.Should().Be("a");

        var noncanonical = keyedA.Replace("\"version\":2", "\"version\": 2", StringComparison.Ordinal);
        var parse = () => ActivityGroupOccurrence.ReadCoordinate(noncanonical);
        parse.Should().Throw<WorkflowConfigurationException>();
        var badIndex = () => ActivityGroupOccurrence.FanOutCoordinate(
            "items", ActivityGroupFanOutIdentityKind.Positional, "01");
        badIndex.Should().Throw<WorkflowConfigurationException>();
    }

    [Fact]
    public void Plan_KeyedReorderingKeepsImmutableEnumerationAndOccurrences()
    {
        var manifest = Manifest(nested: false);
        var binding = new ActivityGroupFanOutBinding(new ActivityGroupMembership("work"), "select");
        var first = Plan(manifest, RunId, "items", binding, ActivityGroupFanOutIdentityKind.Keyed,
            [("a", Hash("alpha")), ("b", Hash("beta"))]);
        var reordered = Plan(manifest, RunId, "items", binding, ActivityGroupFanOutIdentityKind.Keyed,
            [("b", Hash("beta")), ("a", Hash("alpha"))]);

        reordered.CanonicalJson.Should().Be(first.CanonicalJson);
        reordered.PlanDigest.Should().Be(first.PlanDigest);
        reordered.Items.Select(item => item.ItemKey).Should().Equal("a", "b");
        reordered.Items.Select(item => item.Group.Occurrence.GroupOccurrenceId)
            .Should().Equal(first.Items.Select(item => item.Group.Occurrence.GroupOccurrenceId));

        var changedInput = Plan(manifest, RunId, "items", binding, ActivityGroupFanOutIdentityKind.Keyed,
            [("a", Hash("changed")), ("b", Hash("beta"))]);
        changedInput.PlanDigest.Should().NotBe(first.PlanDigest);
    }

    [Fact]
    public void Plan_PositionalReorderingAndScopeChangesProduceDifferentIdentity()
    {
        var manifest = Manifest(nested: false);
        var binding = new ActivityGroupFanOutBinding(new ActivityGroupMembership("work"), "select");
        var positional = Plan(manifest, RunId, "items", binding, ActivityGroupFanOutIdentityKind.Positional,
            [("0", Hash("alpha")), ("1", Hash("beta"))]);
        var reordered = Plan(manifest, RunId, "items", binding, ActivityGroupFanOutIdentityKind.Positional,
            [("0", Hash("beta")), ("1", Hash("alpha"))]);
        var otherPrefix = Plan(manifest, RunId, "other", binding, ActivityGroupFanOutIdentityKind.Positional,
            [("0", Hash("alpha")), ("1", Hash("beta"))]);
        var keyed = Plan(manifest, RunId, "items", binding, ActivityGroupFanOutIdentityKind.Keyed,
            [("0", Hash("alpha")), ("1", Hash("beta"))]);
        var otherRun = Plan(manifest, Guid.NewGuid(), "items", binding, ActivityGroupFanOutIdentityKind.Positional,
            [("0", Hash("alpha")), ("1", Hash("beta"))]);

        reordered.PlanDigest.Should().NotBe(positional.PlanDigest);
        otherPrefix.Items[0].Group.Occurrence.GroupOccurrenceId
            .Should().NotBe(positional.Items[0].Group.Occurrence.GroupOccurrenceId);
        keyed.Items[0].Group.Occurrence.GroupOccurrenceId
            .Should().NotBe(positional.Items[0].Group.Occurrence.GroupOccurrenceId);
        otherRun.Items[0].Group.Occurrence.GroupOccurrenceId
            .Should().NotBe(positional.Items[0].Group.Occurrence.GroupOccurrenceId);
    }

    [Fact]
    public void Plan_BindsParentAndRequiresSelectionDependency()
    {
        var manifest = Manifest(nested: true);
        var parent = ActivityGroupOccurrence.Create(RunId, manifest, "root", null, "[]");
        var binding = new ActivityGroupFanOutBinding(new ActivityGroupMembership("work"), "select", parent);
        var plan = Plan(manifest, RunId, "items", binding, ActivityGroupFanOutIdentityKind.Keyed,
            [("a", Hash("alpha"))]);
        var noDependency = () => Plan(manifest, RunId, "items", binding,
            ActivityGroupFanOutIdentityKind.Keyed, [("a", Hash("alpha"))], dependencies: []);

        plan.Items.Single().Group.Occurrence.ParentGroupOccurrenceId.Should().Be(parent.GroupOccurrenceId);
        noDependency.Should().Throw<WorkflowConfigurationException>()
            .WithMessage("*selection step*");
    }

    [Fact]
    public void Plan_RejectsItemCountAndCanonicalByteOverflow()
    {
        var manifest = Manifest(nested: false);
        var binding = new ActivityGroupFanOutBinding(new ActivityGroupMembership("work"), "select");
        var tooMany = Enumerable.Range(0, ActivityGroupFanOutPlan.MaximumAllowedItems + 1)
            .Select(index => (index.ToString(System.Globalization.CultureInfo.InvariantCulture), Hash(index.ToString())));
        var itemCount = () => Plan(manifest, RunId, "items", binding,
            ActivityGroupFanOutIdentityKind.Keyed, tooMany);

        var largeKeys = Enumerable.Range(0, ActivityGroupFanOutPlan.MaximumAllowedItems)
            .Select(index => (index.ToString("D4", System.Globalization.CultureInfo.InvariantCulture) + new string('x', 508), Hash(index.ToString())));
        var serializedSize = () => Plan(manifest, RunId, "items", binding,
            ActivityGroupFanOutIdentityKind.Keyed, largeKeys);

        itemCount.Should().Throw<WorkflowConfigurationException>();
        serializedSize.Should().Throw<WorkflowConfigurationException>()
            .WithMessage("*byte limit*");
    }

    private static ActivityGroupFanOutPlan Plan(
        ActivityGroupManifest manifest,
        Guid runId,
        string fanOutKey,
        ActivityGroupFanOutBinding binding,
        ActivityGroupFanOutIdentityKind kind,
        IEnumerable<(string Key, string Hash)> items,
        IEnumerable<string>? dependencies = null) =>
        ActivityGroupFanOutPlan.Create(
            runId,
            manifest,
            fanOutKey,
            binding,
            kind,
            typeof(string).FullName!,
            typeof(string).FullName!,
            implementationKey: null,
            declarationHash: null,
            compensationContract: "none",
            dependencies ?? [binding.SelectionStepKey],
            items);

    private static ActivityGroupManifest Manifest(bool nested) => ActivityGroupManifest.Create(
        nested
            ? [
                new ActivityGroupDefinition("root", ActivityGroupKind.Logical, "Root"),
                new ActivityGroupDefinition("work", ActivityGroupKind.Logical, "Work", "root")]
            : [new ActivityGroupDefinition("work", ActivityGroupKind.Logical, "Work")]);

    private static string Hash(string value) => SerializationIdentity.Hash(JsonSerializer.Serialize(value));
}
