namespace Penghou.Zhinu;

/// <summary>Options for previewing and purging retained workflow runs.</summary>
public sealed record RunRetentionOptions
{
    /// <summary>
    /// Cutoff on the authoritative terminal timestamp (<c>completed_at</c>,
    /// falling back to <c>updated_at</c> for legacy rows).
    /// </summary>
    public required DateTimeOffset OlderThan { get; init; }

    /// <summary>
    /// Status set to consider. Defaults to terminal runs only
    /// (completed, failed, cancelled, compensated). Supplying non-terminal
    /// statuses requires <see cref="IncludeActiveRuns"/>.
    /// </summary>
    public IReadOnlyList<WorkflowStatus>? Statuses { get; init; }

    /// <summary>Maximum runs deleted per statement. Defaults to 100.</summary>
    public int BatchSize { get; init; } = 100;

    /// <summary>
    /// Explicit opt-in to delete non-terminal runs. Never implied.
    /// </summary>
    public bool IncludeActiveRuns { get; init; }

    internal void Validate()
    {
        if (BatchSize < 1)
            throw new ArgumentOutOfRangeException(nameof(BatchSize));
        if (Statuses is not null)
        {
            foreach (var status in Statuses)
            {
                if (!Enum.IsDefined(status))
                    throw new ArgumentOutOfRangeException(nameof(Statuses));
                if (!IncludeActiveRuns && !IsTerminal(status))
                    throw new ArgumentException(
                        $"Status '{status}' is not terminal; set {nameof(IncludeActiveRuns)} explicitly.",
                        nameof(Statuses));
            }
        }
    }

    internal static bool IsTerminal(WorkflowStatus status) =>
        status is WorkflowStatus.Completed or WorkflowStatus.Failed or
            WorkflowStatus.Cancelled or WorkflowStatus.Compensated;
}

/// <summary>How many runs retention would delete, with a bounded sample.</summary>
public sealed record RunRetentionPreview
{
    public required int EligibleRunCount { get; init; }
    public required IReadOnlyList<Guid> SampleRunIds { get; init; }
    public required DateTimeOffset EvaluatedAt { get; init; }
}

/// <summary>
/// Terminal-run retention. The legacy <c>PurgeRunsAsync</c> behavior is
/// unchanged; this is the safer operation: terminal-only by default,
/// completion-time cutoff, bounded batches with per-batch revalidation, and
/// no active-run deletion without explicit opt-in. Run deletion cascades to
/// steps, events, artifacts, signals, compensations, operations, and
/// generations via foreign keys. Workflow instances, child runs, agent
/// checkpoints, and external artifact bytes have independent lifecycles and
/// are never deleted by retention.
/// </summary>
public interface IWorkflowRetentionRepository
{
    ValueTask<RunRetentionPreview> PreviewRetentionAsync(
        RunRetentionOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes all eligible runs in bounded batches. Returns the total deleted.</summary>
    ValueTask<int> PurgeRetainedRunsAsync(
        RunRetentionOptions options,
        CancellationToken cancellationToken = default);
}
