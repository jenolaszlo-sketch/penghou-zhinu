namespace Penghou.Zhinu.Testing;

/// <summary>
/// Supplies an isolated durable store (and a peer instance over the same backing
/// store) to the <see cref="WorkflowStoreConformanceSuite"/>. A store
/// implementation's test project provides a fixture so the whole suite runs
/// against it, including cross-instance behavior that approximates separate
/// hosts.
/// </summary>
public interface IWorkflowStoreFixture : IAsyncDisposable
{
    /// <summary>The primary store instance used for the conformance checks.</summary>
    IWorkflowStore Store { get; }

    /// <summary>
    /// A second, independent store instance over the same backing store, used to
    /// exercise cross-instance claims, fencing, and recovery rather than calling
    /// into a single object from two threads.
    /// </summary>
    IWorkflowStore CreatePeerStore();

    /// <summary>Time source used by the suite; a fake provider enables deterministic lease expiry.</summary>
    TimeProvider TimeProvider { get; }
}
