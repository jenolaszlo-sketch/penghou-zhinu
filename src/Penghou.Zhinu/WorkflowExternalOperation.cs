namespace Penghou.Zhinu;

/// <summary>Lifecycle of a durable external-operation handle.</summary>
public enum ExternalOperationStatus
{
    /// <summary>The operation was registered but not yet acquired by a worker.</summary>
    Requested = 0,
    /// <summary>A worker acquired the operation and the external call may be in flight.</summary>
    Running = 1,
    /// <summary>The external call completed and its result was recorded.</summary>
    Completed = 2,
    /// <summary>The external call failed and must not be resumed automatically.</summary>
    Failed = 3,
    /// <summary>The operation was cancelled and must not be resumed automatically.</summary>
    Cancelled = 4
}

/// <summary>What recovery may do with a non-terminal external operation.</summary>
public enum ExternalOperationRecoveryIntent
{
    /// <summary>Reconnect to the in-flight external work without repeating it.</summary>
    Resume = 0,
    /// <summary>Start the external work over under a new attempt.</summary>
    Retry = 1,
    /// <summary>Leave the external work alone; never resume it automatically.</summary>
    Abandon = 2
}

/// <summary>Durable handle linking external provider work to a workflow run.</summary>
public sealed record WorkflowExternalOperation
{
    public required Guid OperationId { get; init; }
    public required Guid WorkflowRunId { get; init; }
    public Guid? StepId { get; init; }
    public string? StepKey { get; init; }
    public int? StepRevision { get; init; }
    public int? Attempt { get; init; }
    public string? IdempotencyKey { get; init; }
    public required string Provider { get; init; }
    public string? ExternalId { get; init; }
    public string? OwnerId { get; init; }
    public required long LeaseGeneration { get; init; }
    public required ExternalOperationStatus Status { get; init; }
    public required ExternalOperationRecoveryIntent RecoveryIntent { get; init; }
    public string? PayloadJson { get; init; }
    public string? Error { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
}

/// <summary>Intent to register a durable external-operation handle.</summary>
public sealed record ExternalOperationRegistration
{
    public required Guid WorkflowRunId { get; init; }
    public Guid? StepId { get; init; }
    public string? StepKey { get; init; }
    public int? StepRevision { get; init; }
    public int? Attempt { get; init; }
    public string? IdempotencyKey { get; init; }
    public required string Provider { get; init; }
    public string? ExternalId { get; init; }
    public required ExternalOperationRecoveryIntent RecoveryIntent { get; init; }
    public string? PayloadJson { get; init; }
}
