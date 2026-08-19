namespace Penghou.Zhinu;

/// <summary>Describes the durable lifecycle state of a workflow run.</summary>
public enum WorkflowStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    Cancelled,

    /// <summary>
    /// The run's forward operations were undone after it completed or failed.
    /// The external history still happened; <c>Compensated</c> records that the
    /// compensating work finished.
    /// </summary>
    Compensated,

    /// <summary>
    /// A rollback-and-restart operation is in progress: the run is being
    /// compensated and its forward state rewound before re-execution. Not a
    /// terminal state; <see cref="WorkflowEngine.ExecuteAsync"/> resumes the
    /// persisted operation until the run returns to <see cref="Pending"/>.
    /// </summary>
    RollingBack
}
