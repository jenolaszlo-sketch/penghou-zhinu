using System.Text.Json;
using FluentAssertions;

namespace Penghou.Zhinu.Tests;

public sealed class ActivityGroupManifestTests
{
    [Fact]
    public void Create_AllowsAnEmptyManifestAndNullableMembership()
    {
        var manifest = ActivityGroupManifest.Create(Array.Empty<ActivityGroupDefinition>());

        manifest.Definitions.Should().BeEmpty();
        manifest.ContractVersion.Should().Be(1);
        manifest.ManifestDigest.Should().MatchRegex("^[0-9a-f]{64}$");
        manifest.ValidateMembership(null);
    }

    [Fact]
    public void Create_AllowsNestedLogicalAndMacroGroupsAndUnknownMacroNames()
    {
        var definitions = new[]
        {
            new ActivityGroupDefinition("outer", ActivityGroupKind.Logical, "Outer"),
            new ActivityGroupDefinition(
                "inner",
                ActivityGroupKind.Macro,
                "Inner",
                parentGroupKey: "outer",
                childOrdinal: 0,
                provenance: new ActivityGroupMacroProvenance(
                    "macro-that-is-not-registered",
                    "fingerprint-value",
                    "invocation-1"))
        };

        var manifest = ActivityGroupManifest.Create(definitions);

        manifest.Definitions.Should().HaveCount(2);
        manifest.ValidateMembership(new ActivityGroupMembership("inner"));
    }

    [Fact]
    public void Create_SnapshotsDefinitionsAndExposesAnImmutableCollection()
    {
        var input = new List<ActivityGroupDefinition>
        {
            new("first", ActivityGroupKind.Logical, "First")
        };
        var manifest = ActivityGroupManifest.Create(input);
        var originalDigest = manifest.ManifestDigest;

        input.Add(new ActivityGroupDefinition("later", ActivityGroupKind.Logical, "Later"));

        manifest.Definitions.Should().ContainSingle()
            .Which.GroupKey.Should().Be("first");
        manifest.ManifestDigest.Should().Be(originalDigest);
        manifest.Definitions.Should().BeAssignableTo<IReadOnlyList<ActivityGroupDefinition>>();
        if (manifest.Definitions is ICollection<ActivityGroupDefinition> collection)
            collection.IsReadOnly.Should().BeTrue();
    }

    [Fact]
    public void Create_UsesCanonicalContentIndependentOfInputEnumerationOrder()
    {
        var definitions = new[]
        {
            new ActivityGroupDefinition("zeta", ActivityGroupKind.Logical, "Zeta", childOrdinal: 1),
            new ActivityGroupDefinition("alpha", ActivityGroupKind.Logical, "Alpha", childOrdinal: 0)
        };

        var forward = ActivityGroupManifest.Create(definitions);
        var reversed = ActivityGroupManifest.Create(definitions.AsEnumerable().Reverse());

        reversed.CanonicalJson.Should().Be(forward.CanonicalJson);
        reversed.ManifestDigest.Should().Be(forward.ManifestDigest);
    }

    [Fact]
    public void Create_PreservesOrdinalAndCaseDistinctionsInKeys()
    {
        var manifest = ActivityGroupManifest.Create(new[]
        {
            new ActivityGroupDefinition("A", ActivityGroupKind.Logical, "Upper", childOrdinal: 0),
            new ActivityGroupDefinition("a", ActivityGroupKind.Logical, "Lower", childOrdinal: 1)
        });

        manifest.ValidateMembership(new ActivityGroupMembership("A"));
        manifest.ValidateMembership(new ActivityGroupMembership("a"));
        var unknown = () => manifest.ValidateMembership(new ActivityGroupMembership("A "));
        unknown.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_ChangesDigestWhenDefinitionMetadataChanges()
    {
        var first = ActivityGroupManifest.Create(new[]
        {
            new ActivityGroupDefinition("work", ActivityGroupKind.Logical, "Work")
        });
        var renamed = ActivityGroupManifest.Create(new[]
        {
            new ActivityGroupDefinition("work", ActivityGroupKind.Logical, "Work v2")
        });

        renamed.ManifestDigest.Should().NotBe(first.ManifestDigest);
    }

    [Fact]
    public void Create_RejectsDuplicateKeysMissingParentsCyclesAndSiblingOrdinalConflicts()
    {
        var duplicateKeys = () => ActivityGroupManifest.Create(new[]
        {
            new ActivityGroupDefinition("same", ActivityGroupKind.Logical, "One"),
            new ActivityGroupDefinition("same", ActivityGroupKind.Logical, "Two")
        });
        duplicateKeys.Should().Throw<ArgumentException>();

        var missingParent = () => ActivityGroupManifest.Create(new[]
        {
            new ActivityGroupDefinition("child", ActivityGroupKind.Logical, "Child", "missing")
        });
        missingParent.Should().Throw<ArgumentException>();

        var cycle = () => ActivityGroupManifest.Create(new[]
        {
            new ActivityGroupDefinition("first", ActivityGroupKind.Logical, "First", "second"),
            new ActivityGroupDefinition("second", ActivityGroupKind.Logical, "Second", "first")
        });
        cycle.Should().Throw<ArgumentException>();

        var duplicateOrdinals = () => ActivityGroupManifest.Create(new[]
        {
            new ActivityGroupDefinition("first", ActivityGroupKind.Logical, "First", childOrdinal: 3),
            new ActivityGroupDefinition("second", ActivityGroupKind.Logical, "Second", childOrdinal: 3)
        });
        duplicateOrdinals.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_EnforcesDepthAndManifestSizeLimits()
    {
        var fourLevels = Enumerable.Range(0, 4)
            .Select(index => new ActivityGroupDefinition(
                $"g{index}",
                ActivityGroupKind.Logical,
                $"Group {index}",
                parentGroupKey: index == 0 ? null : $"g{index - 1}"))
            .ToArray();

        var withinDepth = ActivityGroupManifest.Create(
            fourLevels,
            new ActivityGroupManifestLimits(maximumDepth: 4));
        withinDepth.Definitions.Should().HaveCount(4);

        var tooDeep = () => ActivityGroupManifest.Create(
            fourLevels,
            new ActivityGroupManifestLimits(maximumDepth: 3));
        tooDeep.Should().Throw<ArgumentException>();

        var tooMany = () => ActivityGroupManifest.Create(
            fourLevels,
            new ActivityGroupManifestLimits(maximumGroups: 3));
        tooMany.Should().Throw<ArgumentException>();

        var tooManyBytes = () => ActivityGroupManifest.Create(
            new[] { new ActivityGroupDefinition("one", ActivityGroupKind.Logical, "A display name") },
            new ActivityGroupManifestLimits(maximumCanonicalBytes: 32));
        tooManyBytes.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Limits_RejectNonPositiveAndAboveHardMaximumValues()
    {
        foreach (var create in new Action[]
                 {
                     () => new ActivityGroupManifestLimits(maximumGroups: 0),
                     () => new ActivityGroupManifestLimits(maximumDepth: 0),
                     () => new ActivityGroupManifestLimits(maximumCanonicalBytes: 0),
                     () => new ActivityGroupManifestLimits(maximumGroups: 4097),
                     () => new ActivityGroupManifestLimits(maximumDepth: 129),
                     () => new ActivityGroupManifestLimits(maximumCanonicalBytes: 1_048_577)
                 })
        {
            create.Should().Throw<ArgumentException>();
        }
    }

    [Fact]
    public void Constructors_RequireKindAppropriateProvenanceAndValidEnums()
    {
        var macroWithoutProvenance = () => new ActivityGroupDefinition(
            "macro",
            ActivityGroupKind.Macro,
            "Macro");
        macroWithoutProvenance.Should().Throw<ArgumentException>();

        var logicalWithProvenance = () => new ActivityGroupDefinition(
            "logical",
            ActivityGroupKind.Logical,
            "Logical",
            provenance: new ActivityGroupMacroProvenance("m", "f", "i"));
        logicalWithProvenance.Should().Throw<ArgumentException>();

        var invalidKind = () => new ActivityGroupDefinition(
            "invalid",
            (ActivityGroupKind)99,
            "Invalid");
        invalidKind.Should().Throw<ArgumentException>();

        var invalidRole = () => new ActivityGroupMembership(
            "group",
            (ActivityGroupMemberRole)99);
        invalidRole.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructors_RejectInvalidRequiredTextAndUnsafeSourceReferences()
    {
        var whitespaceKey = () => new ActivityGroupDefinition("  ", ActivityGroupKind.Logical, "Name");
        whitespaceKey.Should().Throw<ArgumentException>();

        var tooLongKey = () => new ActivityGroupDefinition(
            new string('k', 513), ActivityGroupKind.Logical, "Name");
        tooLongKey.Should().Throw<ArgumentException>();

        var tooLongDisplay = () => new ActivityGroupDefinition(
            "key", ActivityGroupKind.Logical, new string('d', 1025));
        tooLongDisplay.Should().Throw<ArgumentException>();

        var controlCharacter = () => new ActivityGroupDefinition(
            "key", ActivityGroupKind.Logical, "bad\nname");
        controlCharacter.Should().Throw<ArgumentException>();

        var invalidSurrogate = () => new ActivityGroupDefinition(
            "key", ActivityGroupKind.Logical, "bad\uD800name");
        invalidSurrogate.Should().Throw<ArgumentException>();

        var tooLongMacroName = () => new ActivityGroupMacroProvenance(
            new string('m', 1025), "fingerprint", "invocation");
        tooLongMacroName.Should().Throw<ArgumentException>();

        foreach (var sourceReference in new[]
                 {
                     "../secret.cs", "folder/../secret.cs", "C:/secret.cs",
                     "/absolute/file.cs", "folder\\file.cs", "folder//file.cs"
                 })
        {
            var action = () => new ActivityGroupMacroProvenance(
                "unknown-macro", "opaque-fingerprint", "invocation", sourceReference: sourceReference);
            action.Should().Throw<ArgumentException>(sourceReference);
        }
    }

    [Fact]
    public void Create_DoesNotChangeLegacyWorkflowDefinitionSerialization()
    {
        var definition = new WorkflowDefinition { Name = "sample", Version = "1" };

        JsonSerializer.Serialize(definition).Should().Be("{\"Name\":\"sample\",\"Version\":\"1\"}");
    }
}
