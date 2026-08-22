namespace Penghou.Zhinu;

/// <summary>
/// Receives a notification after an event is durably committed to the store.
/// Implementations (for example ZeroMQ transports) forward the event to remote
/// subscribers. The store remains authoritative: publishers are a best-effort
/// notification layer, not a source of truth, so subscribers must reconcile
/// with the store (for example by re-reading events after their last sequence).
/// If the publisher throws, the engine wraps the exception in
/// <see cref="WorkflowEventPublisherException"/> and propagates it to the
/// caller; the committed event is unaffected.
/// </summary>
public interface IWorkflowEventPublisher
{
    /// <summary>
    /// Called after <paramref name="event"/> has been committed by the store.
    /// If the implementation throws, the engine wraps the failure in
    /// <see cref="WorkflowEventPublisherException"/> and propagates it to the
    /// emitter. The store remains the authoritative source of events.
    /// </summary>
    Task PublishAsync(
        WorkflowEvent @event,
        CancellationToken cancellationToken = default);
}
