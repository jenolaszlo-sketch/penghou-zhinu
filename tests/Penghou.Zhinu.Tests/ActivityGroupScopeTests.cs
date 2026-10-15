using FluentAssertions;

namespace Penghou.Zhinu.Tests;

public sealed class ActivityGroupScopeTests
{
    private static readonly Guid RunId = Guid.Parse("34ed94d4-879a-4aad-8bd4-b37ec4093d81");

    [Fact]
    public void ValidateBinding_AcceptsRootAncestorAndCurrentIterationOccurrences()
    {
        var manifest = Manifest(
            new ActivityGroupDefinition("root", ActivityGroupKind.Logical, "Root"),
            new ActivityGroupDefinition("work", ActivityGroupKind.Logical, "Work", "root"));
        var root = Occurrence(manifest, "root", parent: null, iteration: null);
        var firstLoop = DurableLoopScope.Root("cycle");
        var iterationOne = firstLoop.Iteration(1);
        var iterationTwo = firstLoop.Iteration(2);
        var nestedIteration = firstLoop.Nest(iterationOne, "repository").Iteration(1);
        var workAtOne = Occurrence(manifest, "work", root, iterationOne);
        var workAtTwo = Occurrence(manifest, "work", root, iterationTwo);

        var rootStepAtRoot = () => Validate(manifest, Binding("root", root), iteration: null);
        var rootStepInsideLoop = () => Validate(manifest, Binding("root", root), iterationOne);
        var ancestorStepInsideNestedLoop = () => Validate(manifest, Binding("work", workAtOne), nestedIteration);
        var iterationStep = () => Validate(manifest, Binding("work", workAtOne), iterationOne);

        rootStepAtRoot.Should().NotThrow();
        rootStepInsideLoop.Should().NotThrow("a root occurrence encloses every loop coordinate");
        ancestorStepInsideNestedLoop.Should().NotThrow("an outer iteration occurrence encloses its nested loop");
        iterationStep.Should().NotThrow("the occurrence coordinate matches the typed iteration");

        // Keep this assertion explicit: the second occurrence has a valid identity in this run,
        // but its scope does not enclose iteration one's step.
        workAtTwo.GroupOccurrenceId.Should().NotBe(workAtOne.GroupOccurrenceId);
    }

    [Fact]
    public void ValidateBinding_RejectsSiblingFutureAndUntypedIterationOccurrences()
    {
        var manifest = Manifest(
            new ActivityGroupDefinition("root", ActivityGroupKind.Logical, "Root"),
            new ActivityGroupDefinition("work", ActivityGroupKind.Logical, "Work", "root"));
        var root = Occurrence(manifest, "root", parent: null, iteration: null);
        var loop = DurableLoopScope.Root("cycle");
        var iterationOne = loop.Iteration(1);
        var futureIteration = loop.Iteration(2);
        var siblingLoopIteration = DurableLoopScope.Root("otherCycle").Iteration(1);
        var workAtOne = Occurrence(manifest, "work", root, iterationOne);
        var workAtFuture = Occurrence(manifest, "work", root, futureIteration);

        var siblingIteration = () => Validate(manifest, Binding("work", workAtOne), siblingLoopIteration);
        var futureScope = () => Validate(manifest, Binding("work", workAtFuture), iterationOne);
        var untypedContext = () => Validate(manifest, Binding("work", workAtOne), iteration: null);

        siblingIteration.Should().Throw<WorkflowConfigurationException>();
        futureScope.Should().Throw<WorkflowConfigurationException>();
        untypedContext.Should().Throw<WorkflowConfigurationException>(
            "a plain WorkflowContext step has no typed iteration scope");
    }

    private static void Validate(
        ActivityGroupManifest manifest,
        ActivityGroupStepBinding binding,
        DurableLoopIterationIdentity? iteration) =>
        ActivityGroupRuntime.ValidateBinding(RunId, manifest, binding, iteration);

    private static ActivityGroupStepBinding Binding(string groupKey, ActivityGroupOccurrence occurrence) =>
        new(new ActivityGroupMembership(groupKey), occurrence);

    private static ActivityGroupOccurrence Occurrence(
        ActivityGroupManifest manifest,
        string groupKey,
        ActivityGroupOccurrence? parent,
        DurableLoopIterationIdentity? iteration) =>
        ActivityGroupOccurrence.Create(RunId, manifest, groupKey, parent, ActivityGroupOccurrence.Coordinate(iteration));

    private static ActivityGroupManifest Manifest(params ActivityGroupDefinition[] definitions) =>
        ActivityGroupManifest.Create(definitions);
}
