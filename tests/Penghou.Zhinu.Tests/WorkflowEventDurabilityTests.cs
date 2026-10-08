using FluentAssertions;

namespace Penghou.Zhinu.Tests;

/// <summary>
/// The event durability classification is a pure function of the event type:
/// committed execution transitions are durable and progress/diagnostic events
/// are advisory. Unknown/application-defined types default to durable because
/// emitted events are committed-transition evidence.
/// </summary>
public sealed class WorkflowEventDurabilityTests
{
    [Theory]
    [InlineData(WorkflowEventTypes.WorkflowStarted)]
    [InlineData(WorkflowEventTypes.WorkflowResumed)]
    [InlineData(WorkflowEventTypes.WorkflowCompleted)]
    [InlineData(WorkflowEventTypes.WorkflowFailed)]
    [InlineData(WorkflowEventTypes.WorkflowCancelled)]
    [InlineData(WorkflowEventTypes.StepStarted)]
    [InlineData(WorkflowEventTypes.StepReused)]
    [InlineData(WorkflowEventTypes.StepCompleted)]
    [InlineData(WorkflowEventTypes.StepFailed)]
    [InlineData(WorkflowEventTypes.RetryScheduled)]
    [InlineData(WorkflowEventTypes.DelayScheduled)]
    [InlineData(WorkflowEventTypes.LeaseRecovered)]
    [InlineData(WorkflowEventTypes.StepRestarted)]
    [InlineData(WorkflowEventTypes.RunForked)]
    [InlineData(WorkflowEventTypes.SignalSent)]
    [InlineData(WorkflowEventTypes.SignalDelivered)]
    [InlineData(WorkflowEventTypes.CompensationStarted)]
    [InlineData(WorkflowEventTypes.CompensationCompleted)]
    [InlineData(WorkflowEventTypes.CompensationFailed)]
    [InlineData(WorkflowEventTypes.WorkflowCompensated)]
    [InlineData(WorkflowEventTypes.WorkflowRestarted)]
    [InlineData(WorkflowEventTypes.ArtifactPublished)]
    [InlineData(WorkflowEventTypes.ArtifactInvalidated)]
    [InlineData(WorkflowEventTypes.StepCompletionRefused)]
    [InlineData(WorkflowEventTypes.LoopIterationCommitted)]
    [InlineData(WorkflowEventTypes.LoopCompleted)]
    [InlineData(WorkflowEventTypes.LoopLimitExceeded)]
    public void BuiltInTransitionTypes_AreDurable(string eventType) =>
        WorkflowEventTypes.Durability(eventType).Should().Be(WorkflowEventDurability.Durable);

    [Fact]
    public void Progress_IsAdvisory() =>
        WorkflowEventTypes.Durability(WorkflowEventTypes.Progress)
            .Should().Be(WorkflowEventDurability.Advisory);

    [Theory]
    [InlineData("application-defined")]
    [InlineData("my.custom.event")]
    public void UnknownAndApplicationTypes_DefaultToDurable(string eventType) =>
        WorkflowEventTypes.Durability(eventType).Should().Be(WorkflowEventDurability.Durable);

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void BlankType_Throws(string eventType) =>
        FluentActions.Invoking(() => WorkflowEventTypes.Durability(eventType))
            .Should().Throw<ArgumentException>();

    [Fact]
    public void EventProjection_ClassifiesByType()
    {
        var runId = Guid.NewGuid();
        var durable = new WorkflowEvent
        {
            Sequence = 1,
            WorkflowRunId = runId,
            EventType = WorkflowEventTypes.StepCompleted,
            Timestamp = DateTimeOffset.UnixEpoch
        };
        var advisory = new WorkflowEvent
        {
            Sequence = 2,
            WorkflowRunId = runId,
            EventType = WorkflowEventTypes.Progress,
            Timestamp = DateTimeOffset.UnixEpoch
        };

        durable.Durability.Should().Be(WorkflowEventDurability.Durable);
        advisory.Durability.Should().Be(WorkflowEventDurability.Advisory);
    }
}
