namespace Penghou.Zhinu;

/// <summary>
/// Centralized legal-transition rules for <see cref="WorkflowRun.Status"/>.
/// Every durable run status change should pass through these rules so the
/// runtime's state machine is auditable in one place instead of scattered
/// repository checks.
/// </summary>
public static class RunStateMachine
{
    /// <summary>States that are terminal: immutable except through explicitly permitted administrative operations.</summary>
    public static bool IsTerminal(WorkflowStatus status) =>
        status is WorkflowStatus.Completed or WorkflowStatus.Failed
            or WorkflowStatus.Cancelled or WorkflowStatus.Compensated;

    public static bool CanTransition(WorkflowStatus from, WorkflowStatus to)
    {
        if (from == to)
            return true;
        return (from, to) switch
        {
            // Claim: pending or re-claim of a running (lease-expired) run.
            (WorkflowStatus.Pending, WorkflowStatus.Running) => true,
            (WorkflowStatus.Running, WorkflowStatus.Running) => true,

            // Execution outcomes.
            (WorkflowStatus.Running, WorkflowStatus.Completed) => true,
            (WorkflowStatus.Running, WorkflowStatus.Failed) => true,
            (WorkflowStatus.Running, WorkflowStatus.Cancelled) => true,
            (WorkflowStatus.Pending, WorkflowStatus.Cancelled) => true,

            // Rollback-and-restart: any non-terminal executable run moves to
            // RollingBack, then rewinds to Pending or fails.
            (WorkflowStatus.Pending, WorkflowStatus.RollingBack) => true,
            (WorkflowStatus.Running, WorkflowStatus.RollingBack) => true,
            (WorkflowStatus.RollingBack, WorkflowStatus.RollingBack) => true,
            (WorkflowStatus.RollingBack, WorkflowStatus.Pending) => true,
            (WorkflowStatus.RollingBack, WorkflowStatus.Failed) => true,

            // Plain rollback claims only completed or failed runs, then
            // compensates them into the terminal Compensated state.
            (WorkflowStatus.Completed, WorkflowStatus.RollingBack) => true,
            (WorkflowStatus.Failed, WorkflowStatus.RollingBack) => true,
            (WorkflowStatus.Completed, WorkflowStatus.Compensated) => true,
            (WorkflowStatus.Failed, WorkflowStatus.Compensated) => true,

            // Administrative restart rewinds a non-Compensated terminal run (or
            // any Pending/Running run) to Pending.
            (WorkflowStatus.Completed, WorkflowStatus.Pending) => true,
            (WorkflowStatus.Failed, WorkflowStatus.Pending) => true,
            (WorkflowStatus.Cancelled, WorkflowStatus.Pending) => true,
            (WorkflowStatus.Pending, WorkflowStatus.Pending) => true,
            (WorkflowStatus.Running, WorkflowStatus.Pending) => true,

            _ => false
        };
    }

    public static void AssertCanTransition(
        WorkflowStatus from,
        WorkflowStatus to,
        Guid workflowRunId)
    {
        if (!CanTransition(from, to))
        {
            throw new WorkflowStateException(
                $"Workflow '{workflowRunId:D}' cannot transition from '{from}' to '{to}'.");
        }
    }
}

/// <summary>
/// Centralized legal-transition rules for <see cref="WorkflowStepRun.Status"/>.
/// A completed step can never return to running; only a fresh revision (via
/// restart or fork) may re-execute it.
/// </summary>
public static class StepStateMachine
{
    public static bool CanTransition(StepStatus from, StepStatus to)
    {
        if (from == to)
            return true;
        return (from, to) switch
        {
            // Claim: fresh (Pending), retry/re-claim (Failed, Waiting, or an
            // expired Running lease) all become Running.
            (StepStatus.Pending, StepStatus.Running) => true,
            (StepStatus.Failed, StepStatus.Running) => true,
            (StepStatus.Waiting, StepStatus.Running) => true,
            (StepStatus.Running, StepStatus.Running) => true,

            // Completion, failure, or a durable wait.
            (StepStatus.Running, StepStatus.Completed) => true,
            (StepStatus.Running, StepStatus.Failed) => true,
            (StepStatus.Running, StepStatus.Waiting) => true,

            // A waiting step completes (delay due, signal delivered) or fails
            // terminally (signal timeout, retries exhausted).
            (StepStatus.Waiting, StepStatus.Completed) => true,
            (StepStatus.Waiting, StepStatus.Failed) => true,

            // A failed step retries from its scheduled available time.
            (StepStatus.Failed, StepStatus.Waiting) => true,

            // Administrative cancellation of any non-terminal step.
            (StepStatus.Pending, StepStatus.Cancelled) => true,
            (StepStatus.Running, StepStatus.Cancelled) => true,
            (StepStatus.Waiting, StepStatus.Cancelled) => true,
            (StepStatus.Failed, StepStatus.Cancelled) => true,

            _ => false
        };
    }

    public static void AssertCanTransition(
        StepStatus from,
        StepStatus to,
        Guid stepId)
    {
        if (!CanTransition(from, to))
        {
            throw new WorkflowStateException(
                $"Step '{stepId:D}' cannot transition from '{from}' to '{to}'.");
        }
    }
}
