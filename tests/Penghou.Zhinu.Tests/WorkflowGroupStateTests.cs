using FluentAssertions;
using Penghou.Workflow.Abstractions;

namespace Penghou.Zhinu.Tests;

public sealed class WorkflowGroupStateTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-18T00:00:00Z");
    private static readonly Guid RunId = Guid.Parse("85cba4d0-e85b-4812-af89-827f5bcbaf62");
    private static WorkflowGroupRunFact Run => new(RunId, "group-state", "1", WorkflowStatus.Running,
        null, null, Now, Now, null, 1, Now.AddMinutes(1), null, false);
    private static WorkflowGroupStepFact Step(string key, StepStatus status, ActivityGroupMemberRole role = ActivityGroupMemberRole.Work,
        int revision = 1, bool current = true) => new(RunId, Guid.NewGuid(), key, revision, 1, status, current,
            "group", role, 1, status == StepStatus.Running ? Now.AddMinutes(1) : null, null, Now,
            status == StepStatus.Completed ? Now : null, null);
    private static WorkflowGroupStateInput Input(params WorkflowGroupStepFact[] steps) => new()
    {
        Run = Run,
        Steps = steps,
        Extent = WorkflowGroupExtent.Closed,
        Members = steps.Where(step => step.IsCurrent).Select(step => new WorkflowGroupSelectedMember(step.StepKey, step.Role!.Value)).ToArray()
    };

    [Theory]
    [InlineData(StepStatus.Pending, StepStatus.Pending, StepStatus.Pending)]
    [InlineData(StepStatus.Pending, StepStatus.Running, StepStatus.Running)]
    [InlineData(StepStatus.Running, StepStatus.Failed, StepStatus.Failed)]
    [InlineData(StepStatus.Waiting, StepStatus.Running, StepStatus.Running)]
    [InlineData(StepStatus.Completed, StepStatus.Waiting, StepStatus.Waiting)]
    [InlineData(StepStatus.Completed, StepStatus.Completed, StepStatus.Completed)]
    [InlineData(StepStatus.Cancelled, StepStatus.Completed, StepStatus.Cancelled)]
    [InlineData(StepStatus.Waiting, StepStatus.Cancelled, StepStatus.Waiting)]
    public void OrderedRulesPreserveMixedCounts(StepStatus first, StepStatus second, StepStatus expected)
    {
        var result = WorkflowGroupStateReducer.Evaluate(Input(Step("a", first), Step("b", second)), Now);
        result.Status.Should().Be(expected);
        result.Work.Known.Should().Be(2);
        result.TotalCount.Should().Be(2);
        (result.Work.Pending + result.Work.Running + result.Work.Waiting + result.Work.Completed + result.Work.Failed + result.Work.Cancelled).Should().Be(2);
    }

    [Theory]
    [InlineData(WorkflowGroupExtent.Open, StepStatus.Pending, null)]
    [InlineData(WorkflowGroupExtent.Closed, StepStatus.Completed, 0)]
    [InlineData(WorkflowGroupExtent.Interrupted, StepStatus.Cancelled, null)]
    public void EmptyOpenSealedAndInterruptedAreDistinct(WorkflowGroupExtent extent, StepStatus expected, int? total)
    {
        var result = WorkflowGroupStateReducer.Evaluate(Input() with { Extent = extent }, Now);
        result.Status.Should().Be(expected);
        result.TotalCount.Should().Be(total);
    }

    [Fact]
    public void ThreeCompletedKnownMembersDoNotCloseOpenExpansion()
    {
        var input = Input(Step("a", StepStatus.Completed), Step("b", StepStatus.Completed), Step("c", StepStatus.Completed));
        var open = WorkflowGroupStateReducer.Evaluate(input with { Extent = WorkflowGroupExtent.Open }, Now);
        var closed = WorkflowGroupStateReducer.Evaluate(input, Now);
        open.Status.Should().Be(StepStatus.Pending);
        open.Reason.Should().Be(WorkflowGroupStateReason.ExpansionOpen);
        open.TotalCount.Should().BeNull();
        open.Work.Completed.Should().Be(3);
        closed.Status.Should().Be(StepStatus.Completed);
        closed.TotalCount.Should().Be(3);
    }

    [Fact]
    public void FailedControlRemainsVisibleWithoutAddingWork()
    {
        var result = WorkflowGroupStateReducer.Evaluate(Input(Step("body", StepStatus.Completed), Step("condition", StepStatus.Failed, ActivityGroupMemberRole.Control)), Now);
        result.Status.Should().Be(StepStatus.Failed);
        result.Work.Completed.Should().Be(1);
        result.Control.Failed.Should().Be(1);
        result.TotalCount.Should().Be(1);
    }

    [Fact]
    public void AttemptsHistoryAndUnselectedFailureDoNotInflateOrPoisonCurrentWork()
    {
        var completed = Step("work", StepStatus.Completed, revision: 2) with { Attempt = 9 };
        var input = Input(completed) with { Steps = [Step("work", StepStatus.Failed, revision: 1, current: false), completed, Step("outside", StepStatus.Failed)] };
        var result = WorkflowGroupStateReducer.Evaluate(input, Now);
        result.Status.Should().Be(StepStatus.Completed);
        result.Work.Known.Should().Be(1);
        result.Work.Completed.Should().Be(1);
    }

    [Fact]
    public void HandledCurrentFailureIsNotErasedByCompletedRun()
    {
        var result = WorkflowGroupStateReducer.Evaluate(Input(Step("failed", StepStatus.Failed)) with { Run = Run with { Status = WorkflowStatus.Completed } }, Now);
        result.Status.Should().Be(StepStatus.Failed);
        result.RunStatus.Should().Be(WorkflowStatus.Completed);
    }

    [Theory]
    [InlineData(WorkflowStatus.Completed)]
    [InlineData(WorkflowStatus.Cancelled)]
    [InlineData(WorkflowStatus.Failed)]
    [InlineData(WorkflowStatus.Compensated)]
    [InlineData(WorkflowStatus.RollingBack)]
    public void ProvenCompletedGroupPreservesForwardResultsAcrossRunContext(WorkflowStatus context)
    {
        var result = WorkflowGroupStateReducer.Evaluate(Input(Step("work", StepStatus.Completed)) with { Run = Run with { Status = context } }, Now);
        result.Status.Should().Be(StepStatus.Completed);
        result.RunStatus.Should().Be(context);
        result.Work.Completed.Should().Be(1);
    }

    [Fact]
    public void PendingDeclaredMemberWithoutRowIsCountedOnce()
    {
        var result = WorkflowGroupStateReducer.Evaluate(Input() with { Members = [new("not-claimed", ActivityGroupMemberRole.Work)] }, Now);
        result.Status.Should().Be(StepStatus.Pending);
        result.Work.Pending.Should().Be(1);
        result.TotalCount.Should().Be(1);
    }

    [Theory]
    [InlineData(WaitKind.Signal, WorkflowGroupStateReason.Signal)]
    [InlineData(WaitKind.Child, WorkflowGroupStateReason.Child)]
    [InlineData(WaitKind.Retry, WorkflowGroupStateReason.Retry)]
    [InlineData(WaitKind.Delay, WorkflowGroupStateReason.Delay)]
    public void WaitKindsAndReadinessRemainDiagnostic(WaitKind kind, WorkflowGroupStateReason reason)
    {
        var step = Step("waiting", StepStatus.Waiting);
        var wait = new WorkflowGroupWaitFact(Guid.NewGuid(), step.StepId, step.StepKey, 1, kind, WaitStatus.Parked, null, null, Now.AddSeconds(1));
        var input = Input(step) with { Waits = [wait] };
        var parked = WorkflowGroupStateReducer.Evaluate(input, Now);
        parked.Status.Should().Be(StepStatus.Waiting);
        parked.Reason.Should().Be(reason);
        var ready = WorkflowGroupStateReducer.Evaluate(input with { Waits = [wait with { Status = WaitStatus.Ready }] }, Now);
        ready.Status.Should().Be(StepStatus.Waiting);
        ready.Reason.Should().Be(WorkflowGroupStateReason.AwaitingDispatch);
        ready.Reasons.Should().Contain(reason);
        var due = WorkflowGroupStateReducer.Evaluate(input, Now.AddSeconds(2));
        due.Reason.Should().Be(kind is WaitKind.Retry or WaitKind.Delay ? WorkflowGroupStateReason.AwaitingDispatch : reason);
    }

    [Theory]
    [InlineData(false, WorkflowGroupStateReason.Approval)]
    [InlineData(true, WorkflowGroupStateReason.AwaitingDispatch)]
    public void ApprovalIsSafeWaitingFactNotExecutionPermission(bool ready, WorkflowGroupStateReason expected)
    {
        var input = Input(Step("approval", StepStatus.Pending)) with
        {
            Approvals = [new("approval", 1, false, ExecutionAuthorizationDecision.ApprovalRequired, ready)]
        };
        var result = WorkflowGroupStateReducer.Evaluate(input, Now);
        result.Status.Should().Be(StepStatus.Waiting);
        result.Reason.Should().Be(expected);
        result.Work.Pending.Should().Be(1);
    }

    [Fact]
    public void OldOrCompensationApprovalDoesNotRewriteForwardCompletion()
    {
        var input = Input(Step("work", StepStatus.Completed, revision: 2)) with
        {
            Approvals = [new("work", 1, false, ExecutionAuthorizationDecision.ApprovalRequired, false), new("work", 2, true, ExecutionAuthorizationDecision.ApprovalRequired, false)]
        };
        WorkflowGroupStateReducer.Evaluate(input, Now).Status.Should().Be(StepStatus.Completed);
    }

    [Fact]
    public void WaitPriorityAndSiblingPermutationAreDeterministic()
    {
        var a = Step("signal", StepStatus.Waiting);
        var b = Step("child", StepStatus.Waiting);
        var input = Input(a, b) with
        {
            Waits = [
            new(Guid.NewGuid(), b.StepId, b.StepKey, 1, WaitKind.Child, WaitStatus.Parked, Guid.NewGuid(), null, null),
            new(Guid.NewGuid(), a.StepId, a.StepKey, 1, WaitKind.Signal, WaitStatus.Parked, null, null, null)]
        };
        var result = WorkflowGroupStateReducer.Evaluate(input, Now);
        result.Reason.Should().Be(WorkflowGroupStateReason.Signal);
        result.Reasons.Should().Contain(WorkflowGroupStateReason.Child);
        var reversed = WorkflowGroupStateReducer.Evaluate(input with { Steps = input.Steps.Reverse().ToArray(), Members = input.Members.Reverse().ToArray(), Waits = input.Waits.Reverse().ToArray() }, Now);
        reversed.Should().BeEquivalentTo(result, options => options.WithStrictOrdering());
    }

    [Fact]
    public void ExpiredLeasesPreserveRunningAndAllActiveKeys()
    {
        var input = Input(Step("z", StepStatus.Running) with { LeaseExpiresAt = Now }, Step("a", StepStatus.Running));
        var result = WorkflowGroupStateReducer.Evaluate(input, Now);
        result.Status.Should().Be(StepStatus.Running);
        result.Reasons.Should().Contain(WorkflowGroupStateReason.AwaitingRecovery);
        result.ActiveStepKeys.Should().Equal("a", "z");
        result.Work.Running.Should().Be(2);
    }

    [Theory]
    [InlineData(WorkflowGroupCoverage.Partial)]
    [InlineData(WorkflowGroupCoverage.Unknown)]
    public void IncompleteCoverageKeepsKnownLowerBoundsWithoutSuccess(WorkflowGroupCoverage coverage)
    {
        var result = WorkflowGroupStateReducer.Evaluate(Input(Step("work", StepStatus.Completed)) with { Coverage = coverage }, Now);
        result.Status.Should().BeNull();
        result.Coverage.Should().Be(coverage);
        result.Work.Completed.Should().Be(1);
        result.TotalCount.Should().BeNull();
    }

    [Fact]
    public void DependencyBlockUsesExternalCurrentRevisions()
    {
        var input = Input(Step("body", StepStatus.Pending)) with { Dependencies = [new("body", "gate")] };
        var blocked = WorkflowGroupStateReducer.Evaluate(input, Now);
        blocked.Status.Should().Be(StepStatus.Pending);
        blocked.IsBlocked.Should().BeTrue();
        blocked.BlockingStepKeys.Should().Equal("gate");
        var ready = WorkflowGroupStateReducer.Evaluate(input with { Steps = [.. input.Steps, Step("gate", StepStatus.Completed)] }, Now);
        ready.IsBlocked.Should().BeFalse();
        ready.Work.Known.Should().Be(1);
    }

    [Fact]
    public void FailedOwningRunBlocksUnfinishedGroupOutsideFailedSubtree()
    {
        var result = WorkflowGroupStateReducer.Evaluate(Input(Step("work", StepStatus.Pending)) with { Run = Run with { Status = WorkflowStatus.Failed }, Extent = WorkflowGroupExtent.Interrupted }, Now);
        result.Status.Should().Be(StepStatus.Pending);
        result.IsBlocked.Should().BeTrue();
        result.Reason.Should().Be(WorkflowGroupStateReason.RunFailed);
    }

    [Theory]
    [InlineData(WorkflowGenerationStatus.Quiescing, StepStatus.Pending, WorkflowGroupStateReason.Quiescing)]
    [InlineData(WorkflowGenerationStatus.Superseded, StepStatus.Cancelled, WorkflowGroupStateReason.Interrupted)]
    public void GenerationContextDoesNotInventStepStates(WorkflowGenerationStatus generation, StepStatus status, WorkflowGroupStateReason reason)
    {
        var input = Input(Step("work", StepStatus.Pending)) with { Generation = new(Guid.NewGuid(), Guid.NewGuid(), 1, generation, null) };
        var result = WorkflowGroupStateReducer.Evaluate(input, Now);
        result.Status.Should().Be(status);
        result.Reason.Should().Be(reason);
        result.Work.Pending.Should().Be(1);
    }

    [Theory]
    [InlineData("duplicate-member")]
    [InlineData("duplicate-current")]
    [InlineData("foreign-run")]
    [InlineData("missing-current")]
    public void InvalidRequiredFactsNeverProduceSuccess(string problem)
    {
        var step = Step("work", StepStatus.Completed);
        var input = Input(step);
        input = problem switch
        {
            "duplicate-member" => input with { Members = [.. input.Members, .. input.Members] },
            "duplicate-current" => input with { Steps = [step, step] },
            "foreign-run" => input with { Steps = [step with { WorkflowRunId = Guid.NewGuid() }] },
            _ => input with { Steps = [step with { IsCurrent = false }] }
        };
        var result = WorkflowGroupStateReducer.Evaluate(input, Now);
        result.Status.Should().BeNull();
        result.Reason.Should().Be(WorkflowGroupStateReason.IncompleteData);
        result.TotalCount.Should().BeNull();
    }
}
