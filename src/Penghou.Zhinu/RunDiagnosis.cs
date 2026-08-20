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
}
