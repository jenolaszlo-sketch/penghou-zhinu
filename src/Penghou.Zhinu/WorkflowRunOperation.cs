namespace Penghou.Zhinu;

/// <summary>
/// The durable phase of a run-scoped maintenance operation (for example
/// rollback-and-restart). Phases are persisted so a crashed worker can resume
/// exactly where the operation stopped.
/// </summary>
public enum WorkflowOperationStatus
{
    /// <summary>The operation was recorded but not yet claimed by a worker.</summary>
    Requested,

    /// <summary>Forward steps with registered compensations are being undone.</summary>
    Compensating,

    /// <summary>Compensation finished; the run's forward state is being rewound.</summary>
    Rewinding,

    /// <summary>The rewound run is being transitioned back to a re-executable state.</summary>
    Restarting,

    /// <summary>The operation finished; the run is re-executable again.</summary>
    Completed,

    /// <summary>The operation failed and must not be resumed automatically.</summary>
    Failed
}

/// <summary>
/// A durable, run-scoped operation record that makes administrative workflows
/// (rollback-and-restart) crash-resumable. The record stores the operation
/// intent (payload) and its current phase.
/// </summary>
public sealed record WorkflowRunOperation
{
    public required Guid OperationId { get; init; }

    public required Guid WorkflowRunId { get; init; }

    public required string OperationType { get; init; }

    public required WorkflowOperationStatus Status { get; init; }

    /// <summary>
    /// Operation-specific intent serialized as JSON (for example the target
    /// step, boundary, actor, and reason of a rollback-and-restart).
    /// </summary>
    public string? PayloadJson { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }
}
