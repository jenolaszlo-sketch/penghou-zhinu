namespace Penghou.Zhinu;

/// <summary>
/// Receives a notification after an event is durably committed to the store.
/// Implementations (for example ZeroMQ transports) forward the event to remote
/// subscribers. The store remains authoritative: publishers are a best-effort
/// notification layer, not a source of truth, so subscribers must reconcile
/// with the store (for example by re-reading events after their last sequence).
/// A failing publisher throws <see cref="WorkflowEventPublisherException"/> to
/// the caller that emitted the event; the committed event is unaffected.
/// </summary>
public interface IWorkflowEventPublisher
{
    /// <summary>
    /// Called after <paramref name="event"/> has been committed by the store.
    /// Implementations must not throw; failures are swallowed and logged by
    /// the engine.
    /// </summary>
    Task PublishAsync(
        WorkflowEvent @event,
        CancellationToken cancellationToken = default);
}
