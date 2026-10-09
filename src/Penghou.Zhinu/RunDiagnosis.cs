namespace Penghou.Zhinu;

/// <summary>Stable reason codes explaining a workflow run's current state.</summary>
public enum RunDiagnosisCode
{
    Terminal,
    ReadyToExecute,
    Executing,
    WaitingForRetry,
    WaitingForDelay,
    WaitingForSignal,
    BlockedByDependencies,
    ExpiredLeaseAwaitingRecovery,
    PermanentlyFailedStep,
    ActiveOperation,
    MissingWorkflowRegistration,
    DeadlineExceeded,
    AwaitingWorker
}

/// <summary>A deterministic, point-in-time explanation of workflow progress.</summary>
public sealed record RunDiagnosis
{
    public required Guid WorkflowRunId { get; init; }
    public required RunDiagnosisCode Code { get; init; }
    public required string Summary { get; init; }
    public string? StepKey { get; init; }
    public DateTimeOffset? Until { get; init; }
    public string? LeaseOwner { get; init; }
    public WorkflowRunOperation? Operation { get; init; }
    public IReadOnlyList<string> BlockingStepKeys { get; init; } = [];

    /// <summary>
    /// Evaluates the diagnosis purely from the supplied rows. The caller must
    /// derive every argument from one consistent read boundary; this function
    /// performs no storage reads and is shared by run diagnosis and the
    /// watermarked snapshot path.
    /// </summary>
    internal static RunDiagnosis Evaluate(
        WorkflowRun run,
        IReadOnlyList<WorkflowStepRun> steps,
        IReadOnlyList<StepDependency> dependencies,
        WorkflowRunOperation? operation,
        DateTimeOffset now,
        bool hasWorkflowRegistration)
    {
        RunDiagnosis Result(
            RunDiagnosisCode code,
            string summary,
            WorkflowStepRun? step = null,
            DateTimeOffset? until = null,
            IReadOnlyList<string>? blocking = null) => new()
            {
                WorkflowRunId = run.Id,
                Code = code,
                Summary = summary,
                StepKey = step?.StepKey,
                Until = until,
                LeaseOwner = step?.LeaseOwner ?? run.LeaseOwner,
                Operation = operation,
                BlockingStepKeys = blocking ?? []
            };

        var terminal = run.Status is WorkflowStatus.Completed or
            WorkflowStatus.Cancelled or WorkflowStatus.Compensated;
        if (terminal)
            return Result(RunDiagnosisCode.Terminal, $"Run is terminal in state '{run.Status}'.");
        var failed = steps.FirstOrDefault(step => step.Status == StepStatus.Failed);
        if (failed is not null)
        {
            return Result(
                RunDiagnosisCode.PermanentlyFailedStep,
                $"Step '{failed.StepKey}' failed permanently after attempt {failed.Attempt}.",
                failed);
        }
        if (run.Status == WorkflowStatus.Failed)
            return Result(RunDiagnosisCode.Terminal, "Run is terminal in state 'Failed'.");
        if (operation is not null || run.Status == WorkflowStatus.RollingBack)
        {
            return Result(
                RunDiagnosisCode.ActiveOperation,
                operation is null
                    ? "The run is rolling back and awaits operation recovery."
                    : $"Operation '{operation.OperationType}' is in phase '{operation.Status}'.");
        }
        if (!hasWorkflowRegistration)
        {
            return Result(
                RunDiagnosisCode.MissingWorkflowRegistration,
                $"Workflow '{run.WorkflowName}' version '{run.WorkflowVersion}' is not registered.");
        }
        if (run.Deadline is { } deadline && deadline <= now)
            return Result(RunDiagnosisCode.DeadlineExceeded, "The workflow deadline has passed.", until: deadline);
        var signal = steps.FirstOrDefault(step =>
            step.Status == StepStatus.Waiting && step.SignalName is not null);
        if (signal is not null)
        {
            return Result(
                RunDiagnosisCode.WaitingForSignal,
                $"Step '{signal.StepKey}' is waiting for signal '{signal.SignalName}'.",
                signal);
        }
        var timedWait = steps
            .Where(step => step.Status == StepStatus.Waiting && step.AvailableAt > now)
            .OrderBy(step => step.AvailableAt)
            .FirstOrDefault();
        if (timedWait is not null)
        {
            var retry = timedWait.Error is not null;
            return Result(
                retry ? RunDiagnosisCode.WaitingForRetry : RunDiagnosisCode.WaitingForDelay,
                retry
                    ? $"Step '{timedWait.StepKey}' is waiting for its retry time."
                    : $"Step '{timedWait.StepKey}' is waiting for its durable delay.",
                timedWait,
                timedWait.AvailableAt);
        }
        var completedKeys = steps
            .Where(step => step.Status == StepStatus.Completed)
            .Select(step => step.StepKey)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var pending in steps.Where(step => step.Status == StepStatus.Pending))
        {
            var blockers = dependencies
                .Where(edge => edge.StepKey == pending.StepKey &&
                    !completedKeys.Contains(edge.DependsOnStepKey))
                .Select(edge => edge.DependsOnStepKey)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            if (blockers.Length > 0)
            {
                return Result(
                    RunDiagnosisCode.BlockedByDependencies,
                    $"Step '{pending.StepKey}' is blocked by {blockers.Length} incomplete dependency step(s).",
                    pending,
                    blocking: blockers);
            }
        }
        var leasedStep = steps.FirstOrDefault(step =>
            step.Status == StepStatus.Running && step.LeaseExpiresAt > now);
        if (leasedStep is not null || run.LeaseExpiresAt > now)
        {
            return Result(
                RunDiagnosisCode.Executing,
                leasedStep is null
                    ? "The run is actively leased by a worker."
                    : $"Step '{leasedStep.StepKey}' is actively leased by a worker.",
                leasedStep,
                leasedStep?.LeaseExpiresAt ?? run.LeaseExpiresAt);
        }
        var expiredStep = steps.FirstOrDefault(step =>
            step.Status == StepStatus.Running &&
            (step.LeaseExpiresAt is null || step.LeaseExpiresAt <= now));
        if (expiredStep is not null ||
            run.Status == WorkflowStatus.Running &&
            (run.LeaseExpiresAt is null || run.LeaseExpiresAt <= now))
        {
            return Result(
                RunDiagnosisCode.ExpiredLeaseAwaitingRecovery,
                expiredStep is null
                    ? "The run lease expired and awaits recovery."
                    : $"Step '{expiredStep.StepKey}' has an expired lease and awaits recovery.",
                expiredStep);
        }
        if (run.Status == WorkflowStatus.Pending)
            return Result(RunDiagnosisCode.ReadyToExecute, "The run is pending and ready for a worker.");
        return Result(RunDiagnosisCode.AwaitingWorker, "The run has no active lease and awaits a worker.");
    }
}
