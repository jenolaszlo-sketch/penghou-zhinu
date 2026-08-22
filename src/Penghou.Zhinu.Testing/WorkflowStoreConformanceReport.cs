namespace Penghou.Zhinu.Testing;

/// <summary>
/// Capability groups covered by <see cref="WorkflowStoreConformanceSuite"/>.
/// A store implementation can see which groups it passes rather than treating
/// conformance as a single pass/fail.
/// </summary>
public enum WorkflowConformanceCapability
{
    /// <summary>Run and step round-trips, completion, and result retrieval.</summary>
    Core,

    /// <summary>Concurrent claims have exactly one winner.</summary>
    Concurrency,

    /// <summary>Stale generations can never mutate durable state.</summary>
    Fencing,

    /// <summary>Expired leases are recoverable by a fresh instance.</summary>
    Recovery,

    /// <summary>Signals are buffered and delivered exactly once.</summary>
    Signals,

    /// <summary>Artifacts publish idempotently with provenance.</summary>
    Artifacts,

    /// <summary>Child creation is deterministic and reusable.</summary>
    Children,

    /// <summary>Restart/rollback transitions are atomic and deterministic.</summary>
    Transactions
}

/// <summary>The outcome of one conformance capability group.</summary>
public sealed record WorkflowConformanceGroupResult(
    WorkflowConformanceCapability Capability,
    bool Passed,
    Exception? Error);

/// <summary>The aggregate result of running the conformance suite against a fixture.</summary>
public sealed record WorkflowStoreConformanceReport
{
    public required IReadOnlyList<WorkflowConformanceGroupResult> Groups { get; init; }

    public bool AllPassed => Groups.All(group => group.Passed);

    public IEnumerable<WorkflowConformanceGroupResult> FailedGroups =>
        Groups.Where(group => !group.Passed);

    public override string ToString() =>
        string.Join('\n', Groups.Select(group =>
            $"{(group.Passed ? "PASS" : "FAIL")} {group.Capability}" +
            (group.Error is null ? string.Empty : $" — {group.Error.Message}")));
}
