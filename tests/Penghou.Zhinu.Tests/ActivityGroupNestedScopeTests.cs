using FluentAssertions;

namespace Penghou.Zhinu.Tests;

public sealed class ActivityGroupNestedScopeTests
{
    private static readonly Guid RunId = Guid.Parse("34ed94d4-879a-4aad-8bd4-b37ec4093d81");
    private static ActivityGroupManifest Manifest() => ActivityGroupManifest.Create([
        new ActivityGroupDefinition("outer", ActivityGroupKind.Logical, "Outer"),
        new ActivityGroupDefinition("inner", ActivityGroupKind.Logical, "Inner", "outer")]);

    [Fact]
    public void FullTypedLineage_DistinguishesSameLocalInnerPositionAndRejectsWrongParent()
    {
        var manifest = Manifest();
        var scope = DurableLoopScope.Root("outer");
        var first = scope.Iteration(1);
        var second = scope.Iteration(2);
        var firstInner = scope.Nest(first, "inner").Iteration(1);
        var secondInner = scope.Nest(second, "inner").Iteration(1);
        var parent = ActivityGroupOccurrence.Create(RunId, manifest, "outer", null, ActivityGroupOccurrence.Coordinate(first));
        var child = ActivityGroupOccurrence.Create(RunId, manifest, "inner", parent, ActivityGroupOccurrence.Coordinate(firstInner));
        ActivityGroupOccurrence.Coordinate(firstInner).Should().Be("[{\"loop\":\"outer\",\"iteration\":1},{\"loop\":\"inner\",\"iteration\":1}]");
        var wrongParent = () => ActivityGroupOccurrence.Create(RunId, manifest, "inner", parent, ActivityGroupOccurrence.Coordinate(secondInner));
        wrongParent.Should().Throw<WorkflowConfigurationException>();
        var siblingBinding = () => ActivityGroupRuntime.ValidateBinding(RunId, manifest,
            new ActivityGroupStepBinding(new ActivityGroupMembership("inner"), child), secondInner);
        siblingBinding.Should().Throw<WorkflowConfigurationException>();
        var secondParent = ActivityGroupOccurrence.Create(RunId, manifest, "outer", null, ActivityGroupOccurrence.Coordinate(second));
        var secondChild = ActivityGroupOccurrence.Create(RunId, manifest, "inner", secondParent, ActivityGroupOccurrence.Coordinate(secondInner));
        secondChild.GroupOccurrenceId.Should().NotBe(child.GroupOccurrenceId);
    }

    [Theory]
    [InlineData(ActivityGroupFanOutIdentityKind.Keyed)]
    [InlineData(ActivityGroupFanOutIdentityKind.Positional)]
    public void GroupedFanOutUnderNestedLoopParent_RejectsBeforeItemExecution(ActivityGroupFanOutIdentityKind kind)
    {
        var manifest = Manifest();
        var scope = DurableLoopScope.Root("outer");
        var outer = scope.Iteration(2);
        var inner = scope.Nest(outer, "inner").Iteration(1);
        var parent = ActivityGroupOccurrence.Create(RunId, manifest, "outer", null, ActivityGroupOccurrence.Coordinate(inner));
        var create = () => ActivityGroupFanOutPlan.Create(RunId, manifest, "items",
            new ActivityGroupFanOutBinding(new ActivityGroupMembership("inner"), "select", parent), kind,
            "input", "output", null, null, "none", ["select"], [(kind == ActivityGroupFanOutIdentityKind.Keyed ? "a" : "0", "hash")]);
        create.Should().Throw<WorkflowConfigurationException>().WithMessage("*root execution scope only*");
    }

    [Fact]
    public void NestedLoopAndFanOutCoordinate_CannotBeCombinedIntoCompetingScope()
    {
        var manifest = Manifest();
        var outer = DurableLoopScope.Root("outer");
        var iteration = outer.Iteration(1);
        var nested = outer.Nest(iteration, "inner").Iteration(1);
        var occurrence = ActivityGroupOccurrence.Create(RunId, manifest, "outer", null, ActivityGroupOccurrence.Coordinate(iteration));
        var bind = () => ActivityGroupRuntime.ValidateBinding(RunId, manifest,
            new ActivityGroupStepBinding(new ActivityGroupMembership("outer"), occurrence), nested,
            ActivityGroupOccurrence.FanOutCoordinate("items", ActivityGroupFanOutIdentityKind.Keyed, "a"));
        bind.Should().Throw<WorkflowConfigurationException>().WithMessage("*Nested grouped fan-out scopes*");
    }
}
