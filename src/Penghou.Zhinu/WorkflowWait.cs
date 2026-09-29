namespace Penghou.Zhinu;

/// <summary>What a parked wait is waiting for.</summary>
public enum WaitKind
{
    /// <summary>An external signal delivery.</summary>
    Signal = 0,
    /// <summary>A retry backoff becoming due.</summary>
    Retry = 1,
    /// <summary>A child run reaching a terminal state.</summary>
    Child = 2,
    /// <summary>A durable delay becoming due.</summary>
    Delay = 3
}

/// <summary>Lifecycle of one persisted wait record.</summary>
public enum WaitStatus
{
    /// <summary>Parked: the worker released capacity; scheduler skips until ready or due.</summary>
    Parked = 0,
    /// <summary>A signal arrived or a child completed; eligible for admission.</summary>
    Ready = 1,
    /// <summary>Consumed by delivery or a due retry.</summary>
    Completed = 2,
    /// <summary>Abandoned by cancellation or expiry without delivery.</summary>
    Cancelled = 3
}

/// <summary>One persisted wait record. Exactly one row exists per run and step key.</summary>
public sealed record WorkflowWait
{
    public required Guid WaitId { get; init; }
    public required Guid WorkflowRunId { get; init; }
    public required string StepKey { get; init; }
    public required int StepRevision { get; init; }
    public required Guid StepId { get; init; }
    public required WaitKind Kind { get; init; }
    public string? SignalName { get; init; }
    public Guid? ChildRunId { get; init; }
    public DateTimeOffset? DeadlineAt { get; init; }
    public DateTimeOffset? AvailableAt { get; init; }
    public required WaitStatus Status { get; init; }
    public required long LeaseGeneration { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>Request to park a wait. Deadlines persist on first park and are kept across resumes and restarts.</summary>
public sealed record ParkWaitRequest
{
    public required Guid WorkflowRunId { get; init; }
    public required string StepKey { get; init; }
    public required int StepRevision { get; init; }
    public required Guid StepId { get; init; }
    public required WaitKind Kind { get; init; }
    public string? SignalName { get; init; }
    public Guid? ChildRunId { get; init; }
    public TimeSpan? Timeout { get; init; }
    public DateTimeOffset? AvailableAt { get; init; }
    public required long LeaseGeneration { get; init; }
    public required DateTimeOffset Now { get; init; }
}

/// <summary>
/// Durable wait records. Parking replaces any older row for the run and step
/// key, keeping an existing deadline so resumes never reset it and restarts
/// never extend it. Rows are never deleted except by run cascade.
/// </summary>
public interface IWorkflowWaitRepository
{
    ValueTask<WorkflowWait> ParkWaitAsync(
        ParkWaitRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<WorkflowWait?> GetWaitAsync(
        Guid workflowRunId,
        string stepKey,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<WorkflowWait>> ListWaitsAsync(
        Guid workflowRunId,
        CancellationToken cancellationToken = default);

    ValueTask CompleteWaitAsync(
        Guid waitId,
        CancellationToken cancellationToken = default);

    ValueTask MarkSignalWaitsReadyAsync(
        Guid workflowRunId,
        string signalName,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    ValueTask MarkChildWaitsReadyAsync(
        Guid childRunId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    /// <summary>Whether the run has parked waits owned by the given generation.</summary>
    ValueTask<bool> HasParkedWaitsAsync(
        Guid workflowRunId,
        long leaseGeneration,
        CancellationToken cancellationToken = default);
}
