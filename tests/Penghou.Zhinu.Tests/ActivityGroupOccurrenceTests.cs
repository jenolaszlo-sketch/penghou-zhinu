using FluentAssertions;
using System.Text.Json;

namespace Penghou.Zhinu.Tests;

public sealed class ActivityGroupOccurrenceTests
{
    private static readonly Guid RunId = Guid.Parse("8e40316e-63f4-4a63-ae49-2b39cc6d9b6f");

    [Fact]
    public void Create_RootOccurrenceIsDeterministicAndIgnoresDisplayAndProvenance()
    {
        var firstManifest = Manifest(
            new ActivityGroupDefinition("root", ActivityGroupKind.Logical, "Delivery"));
        var renamedManifest = Manifest(
            new ActivityGroupDefinition(
                "root",
                ActivityGroupKind.Macro,
                "Dispatch",
                provenance: new ActivityGroupMacroProvenance(
                    "unknown-macro",
                    "definition-revision-2",
                    "invocation-b")));

        var first = Create(RunId, firstManifest, "root", null, "[]");
        var repeated = Create(RunId, firstManifest, "root", null, "[]");
        var renamed = Create(RunId, renamedManifest, "root", null, "[]");

        first.GroupOccurrenceId.Should().Be(repeated.GroupOccurrenceId);
        renamed.GroupOccurrenceId.Should().Be(first.GroupOccurrenceId);
        renamed.ManifestDigest.Should().NotBe(first.ManifestDigest);
    }

    [Fact]
    public void Create_DistinguishesRunDefinitionParentAndCoordinate()
    {
        var manifest = Manifest(
            new ActivityGroupDefinition("root", ActivityGroupKind.Logical, "Root", childOrdinal: 0),
            new ActivityGroupDefinition("other", ActivityGroupKind.Logical, "Other", childOrdinal: 1),
            new ActivityGroupDefinition("child", ActivityGroupKind.Logical, "Child", "root"));
        var root = Create(RunId, manifest, "root", null, "[]");
        var other = Create(RunId, manifest, "other", null, "[]");
        var child = Create(RunId, manifest, "child", root, "[]");
        var otherParent = Create(RunId, manifest, "root", null, "[{\"loop\":\"cycle\",\"iteration\":1}]");
        var childUnderOtherOccurrence = Create(RunId, manifest, "child", otherParent, "[{\"loop\":\"cycle\",\"iteration\":1}]");

        var otherRun = Create(Guid.NewGuid(), manifest, "root", null, "[]");
        var childAtAnotherCoordinate = Create(
            RunId,
            manifest,
            "child",
            root,
            "[{\"loop\":\"cycle\",\"iteration\":1}]");

        root.GroupOccurrenceId.Should().NotBe(otherRun.GroupOccurrenceId);
        root.GroupOccurrenceId.Should().NotBe(other.GroupOccurrenceId);
        child.GroupOccurrenceId.Should().NotBe(childUnderOtherOccurrence.GroupOccurrenceId);
        child.GroupOccurrenceId.Should().NotBe(childAtAnotherCoordinate.GroupOccurrenceId);
    }

    [Fact]
    public void Create_UsesNestedLoopIterationCoordinatesInOrder()
    {
        var manifest = Manifest(
            new ActivityGroupDefinition("root", ActivityGroupKind.Logical, "Root"),
            new ActivityGroupDefinition("work", ActivityGroupKind.Logical, "Work", "root"));
        var root = Create(RunId, manifest, "root", null, "[]");
        var outer = DurableLoopScope.Root("reviewCycle");
        var inner = outer.Nest(outer.Iteration(1), "repository");

        var first = Create(
            RunId,
            manifest,
            "work",
            root,
            ActivityGroupOccurrence.Coordinate(inner.Iteration(1)));
        var secondOuterIteration = outer.Nest(outer.Iteration(2), "repository");
        var second = Create(
            RunId,
            manifest,
            "work",
            root,
            ActivityGroupOccurrence.Coordinate(secondOuterIteration.Iteration(1)));

        first.ExecutionCoordinate.Should().Be(
            "[{\"loop\":\"reviewCycle\",\"iteration\":1},{\"loop\":\"repository\",\"iteration\":1}]");
        second.ExecutionCoordinate.Should().Be(
            "[{\"loop\":\"reviewCycle\",\"iteration\":2},{\"loop\":\"repository\",\"iteration\":1}]");
        first.GroupOccurrenceId.Should().NotBe(second.GroupOccurrenceId);
    }

    [Fact]
    public void Create_RequiresExplicitParentFromSameRunAndManifestWithCoordinatePrefix()
    {
        var manifest = Manifest(
            new ActivityGroupDefinition("root", ActivityGroupKind.Logical, "Root"),
            new ActivityGroupDefinition("child", ActivityGroupKind.Logical, "Child", "root"));
        var root = Create(
            RunId,
            manifest,
            "root",
            null,
            "[{\"loop\":\"cycle\",\"iteration\":1}]");

        var noParent = () => Create(RunId, manifest, "child", null, "[]");
        var wrongRun = () => Create(
            RunId,
            manifest,
            "child",
            Create(Guid.NewGuid(), manifest, "root", null, "[]"),
            "[]");
        var wrongManifest = () => Create(
            RunId,
            manifest,
            "child",
            Create(
                RunId,
                Manifest(
                    new ActivityGroupDefinition("root", ActivityGroupKind.Logical, "Changed"),
                    new ActivityGroupDefinition("child", ActivityGroupKind.Logical, "Child", "root")),
                "root",
                null,
                "[]"),
            "[]");
        var nonAncestorCoordinate = () => Create(RunId, manifest, "child", root, "[]");

        noParent.Should().Throw<WorkflowConfigurationException>();
        wrongRun.Should().Throw<WorkflowConfigurationException>();
        wrongManifest.Should().Throw<WorkflowConfigurationException>();
        nonAncestorCoordinate.Should().Throw<WorkflowConfigurationException>();
    }

    [Fact]
    public void StepBinding_RequiresMembershipForSelectedOccurrence()
    {
        var manifest = Manifest(new ActivityGroupDefinition("work", ActivityGroupKind.Logical, "Work"));
        var occurrence = Create(RunId, manifest, "work", null, "[]");

        var binding = new ActivityGroupStepBinding(
            new ActivityGroupMembership("work", ActivityGroupMemberRole.Work),
            occurrence);
        var wrongMembership = () => new ActivityGroupStepBinding(
            new ActivityGroupMembership("other", ActivityGroupMemberRole.Work),
            occurrence);

        binding.Occurrence.Should().BeSameAs(occurrence);
        binding.Membership.GroupKey.Should().Be("work");
        wrongMembership.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ContextAndLoopFactoriesBindRootAndRepeatedIterationScopes()
    {
        var manifest = Manifest(
            new ActivityGroupDefinition("root", ActivityGroupKind.Logical, "Root"),
            new ActivityGroupDefinition("work", ActivityGroupKind.Logical, "Work", "root"));
        var context = CreateContext(RunId, manifest);
        var root = context.GetActivityGroup("root");
        var firstIteration = new WorkflowLoopIteration<int>(
            context,
            DurableLoopScope.Root("cycle").Iteration(1),
            0,
            "condition-1");
        var secondIteration = new WorkflowLoopIteration<int>(
            context,
            DurableLoopScope.Root("cycle").Iteration(2),
            0,
            "condition-2");

        var first = firstIteration.GetActivityGroup("work", root);
        var second = secondIteration.GetActivityGroup("work", root);

        root.ExecutionCoordinate.Should().Be("[]");
        first.ExecutionCoordinate.Should().Be("[{\"loop\":\"cycle\",\"iteration\":1}]");
        second.ExecutionCoordinate.Should().Be("[{\"loop\":\"cycle\",\"iteration\":2}]");
        first.GroupOccurrenceId.Should().NotBe(second.GroupOccurrenceId);
    }

    [Fact]
    public void ContextFactory_RejectsMissingManifest()
    {
        var context = CreateContext(RunId, manifest: null);

        var create = () => context.GetActivityGroup("work");

        create.Should().Throw<WorkflowConfigurationException>();
    }

    private static ActivityGroupManifest Manifest(params ActivityGroupDefinition[] definitions) =>
        ActivityGroupManifest.Create(definitions);

    private static WorkflowContext CreateContext(Guid runId, ActivityGroupManifest? manifest) =>
        new(
            runId,
            store: null!,
            ownerId: "test-owner",
            options: new ZhinuOptions(),
            serializerOptions: new JsonSerializerOptions(),
            timeProvider: TimeProvider.System,
            leaseGeneration: 1,
            workflowCancellationToken: CancellationToken.None,
            groupManifest: manifest);

    private static ActivityGroupOccurrence Create(
        Guid runId,
        ActivityGroupManifest manifest,
        string groupKey,
        ActivityGroupOccurrence? parent,
        string coordinate) =>
        ActivityGroupOccurrence.Create(runId, manifest, groupKey, parent, coordinate);
}
