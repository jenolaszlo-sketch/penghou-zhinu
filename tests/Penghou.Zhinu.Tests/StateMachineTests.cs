using FluentAssertions;
#pragma warning disable xUnit1051

namespace Penghou.Zhinu.Tests;

public sealed class StateMachineTests
{
    [Fact]
    public void RunStateMachine_LegalTransitions()
    {
        (RunStateMachine.CanTransition(WorkflowStatus.Pending, WorkflowStatus.Running)).Should().BeTrue();
        (RunStateMachine.CanTransition(WorkflowStatus.Running, WorkflowStatus.Completed)).Should().BeTrue();
        (RunStateMachine.CanTransition(WorkflowStatus.Running, WorkflowStatus.Failed)).Should().BeTrue();
        (RunStateMachine.CanTransition(WorkflowStatus.Running, WorkflowStatus.Cancelled)).Should().BeTrue();
        (RunStateMachine.CanTransition(WorkflowStatus.Completed, WorkflowStatus.Compensated)).Should().BeTrue();
        (RunStateMachine.CanTransition(WorkflowStatus.Failed, WorkflowStatus.Compensated)).Should().BeTrue();
        (RunStateMachine.CanTransition(WorkflowStatus.Completed, WorkflowStatus.RollingBack)).Should().BeTrue();
        (RunStateMachine.CanTransition(WorkflowStatus.Failed, WorkflowStatus.RollingBack)).Should().BeTrue();
        (RunStateMachine.CanTransition(WorkflowStatus.RollingBack, WorkflowStatus.Pending)).Should().BeTrue();
        (RunStateMachine.CanTransition(WorkflowStatus.Completed, WorkflowStatus.Pending)).Should().BeTrue();
        (RunStateMachine.CanTransition(WorkflowStatus.Failed, WorkflowStatus.Pending)).Should().BeTrue();
    }

    [Fact]
    public void RunStateMachine_CompletedStepCannotReturnToRunning()
    {
        (RunStateMachine.CanTransition(WorkflowStatus.Completed, WorkflowStatus.Running)).Should().BeFalse();
    }

    [Fact]
    public void RunStateMachine_CompensatedIsImmutable()
    {
        (RunStateMachine.CanTransition(WorkflowStatus.Compensated, WorkflowStatus.Pending)).Should().BeFalse();
        (RunStateMachine.CanTransition(WorkflowStatus.Compensated, WorkflowStatus.Completed)).Should().BeFalse();
        (RunStateMachine.CanTransition(WorkflowStatus.Compensated, WorkflowStatus.RollingBack)).Should().BeFalse();
    }

    [Fact]
    public void RunStateMachine_CancelledCannotTransitionToRollingBackOrCompensated()
    {
        (RunStateMachine.CanTransition(WorkflowStatus.Cancelled, WorkflowStatus.RollingBack)).Should().BeFalse();
        (RunStateMachine.CanTransition(WorkflowStatus.Cancelled, WorkflowStatus.Compensated)).Should().BeFalse();
    }

    [Fact]
    public void RunStateMachine_PendingCannotTransitionToCompletedOrCompensated()
    {
        (RunStateMachine.CanTransition(WorkflowStatus.Pending, WorkflowStatus.Completed)).Should().BeFalse();
        (RunStateMachine.CanTransition(WorkflowStatus.Pending, WorkflowStatus.Compensated)).Should().BeFalse();
    }

    [Fact]
    public void RunStateMachine_IsTerminal()
    {
        RunStateMachine.IsTerminal(WorkflowStatus.Completed).Should().BeTrue();
        RunStateMachine.IsTerminal(WorkflowStatus.Failed).Should().BeTrue();
        RunStateMachine.IsTerminal(WorkflowStatus.Cancelled).Should().BeTrue();
        RunStateMachine.IsTerminal(WorkflowStatus.Compensated).Should().BeTrue();
        RunStateMachine.IsTerminal(WorkflowStatus.Pending).Should().BeFalse();
        RunStateMachine.IsTerminal(WorkflowStatus.Running).Should().BeFalse();
        RunStateMachine.IsTerminal(WorkflowStatus.RollingBack).Should().BeFalse();
    }

    [Fact]
    public void RunStateMachine_AssertThrowsOnIllegal()
    {
        var act = () => RunStateMachine.AssertCanTransition(
            WorkflowStatus.Completed,
            WorkflowStatus.Running,
            Guid.NewGuid());
        act.Should().Throw<WorkflowStateException>();
    }

    [Fact]
    public void StepStateMachine_LegalTransitions()
    {
        (StepStateMachine.CanTransition(StepStatus.Pending, StepStatus.Running)).Should().BeTrue();
        (StepStateMachine.CanTransition(StepStatus.Running, StepStatus.Completed)).Should().BeTrue();
        (StepStateMachine.CanTransition(StepStatus.Running, StepStatus.Failed)).Should().BeTrue();
        (StepStateMachine.CanTransition(StepStatus.Running, StepStatus.Waiting)).Should().BeTrue();
        (StepStateMachine.CanTransition(StepStatus.Waiting, StepStatus.Completed)).Should().BeTrue();
        (StepStateMachine.CanTransition(StepStatus.Waiting, StepStatus.Failed)).Should().BeTrue();
        (StepStateMachine.CanTransition(StepStatus.Waiting, StepStatus.Running)).Should().BeTrue();
        (StepStateMachine.CanTransition(StepStatus.Failed, StepStatus.Running)).Should().BeTrue();
        (StepStateMachine.CanTransition(StepStatus.Failed, StepStatus.Waiting)).Should().BeTrue();
    }

    [Fact]
    public void StepStateMachine_CompletedStepCannotTransition()
    {
        (StepStateMachine.CanTransition(StepStatus.Completed, StepStatus.Running)).Should().BeFalse();
        (StepStateMachine.CanTransition(StepStatus.Completed, StepStatus.Failed)).Should().BeFalse();
        (StepStateMachine.CanTransition(StepStatus.Completed, StepStatus.Waiting)).Should().BeFalse();
    }

    [Fact]
    public void StepStateMachine_PendingCannotCompleteOrFailDirectly()
    {
        (StepStateMachine.CanTransition(StepStatus.Pending, StepStatus.Completed)).Should().BeFalse();
        (StepStateMachine.CanTransition(StepStatus.Pending, StepStatus.Failed)).Should().BeFalse();
    }

    [Fact]
    public void StepStateMachine_CancelledIsImmutable()
    {
        (StepStateMachine.CanTransition(StepStatus.Cancelled, StepStatus.Running)).Should().BeFalse();
        (StepStateMachine.CanTransition(StepStatus.Cancelled, StepStatus.Completed)).Should().BeFalse();
    }
}
