using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Penghou.Zhinu.Subscriptions;

/// <summary>
/// Owns the per-run wakeup channels that bridge in-process event notifications
/// to <see cref="WorkflowEngine.SubscribeAsync"/>. A subscription owns its
/// run's channel for its lifetime and releases it on exit, so abandoned
/// subscriptions cannot grow the channel set unbounded. Concurrent
/// subscribers for the same run share the channel and re-create it on demand.
/// </summary>
internal sealed class EventSubscriptionHub
{
    private readonly ConcurrentDictionary<Guid, Channel<byte>> channels = new();

    /// <summary>Number of live run wakeup channels.</summary>
    public int Count => channels.Count;

    /// <summary>
    /// Takes ownership of the run's wakeup channel. Disposing the returned
    /// lease releases it so a later subscriber can re-create it on demand.
    /// </summary>
    public IDisposable Own(Guid workflowRunId) => new Ownership(this, workflowRunId);

    /// <summary>Signals the run's current subscriber, if one is waiting.</summary>
    public void Notify(Guid workflowRunId)
    {
        if (channels.TryGetValue(workflowRunId, out var channel))
            channel.Writer.TryWrite(0);
    }

    /// <summary>
    /// Waits for an in-process notification or the poll interval, whichever
    /// comes first, then cancels the losing wait and drains any notification
    /// byte. Cancelling the loser keeps quiet polls from accumulating pending
    /// channel readers; the poll fallback observes events appended by other
    /// processes or before this subscriber existed.
    /// </summary>
    public async ValueTask WaitForNotificationOrPollAsync(
        Guid workflowRunId,
        TimeSpan pollInterval,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var channel = channels.GetOrAdd(
            workflowRunId,
            _ => Channel.CreateBounded<byte>(
                new BoundedChannelOptions(1)
                {
                    FullMode = BoundedChannelFullMode.DropWrite
                }));
        using var iterationTimeout =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var notify = channel.Reader.WaitToReadAsync(iterationTimeout.Token).AsTask();
        var poll = Task.Delay(pollInterval, timeProvider, iterationTimeout.Token);
        await Task.WhenAny(notify, poll).ConfigureAwait(false);
        await iterationTimeout.CancelAsync().ConfigureAwait(false);
        // Drain the notification byte so the next wait actually blocks instead
        // of busy-spinning on a stale signal.
        channel.Reader.TryRead(out _);
    }

    /// <summary>Completes every wakeup channel and forgets them.</summary>
    public void CompleteAll()
    {
        foreach (var channel in channels.Values)
            channel.Writer.TryComplete();
        channels.Clear();
    }

    private void Release(Guid workflowRunId) => channels.TryRemove(workflowRunId, out _);

    private sealed class Ownership(EventSubscriptionHub hub, Guid workflowRunId) : IDisposable
    {
        public void Dispose() => hub.Release(workflowRunId);
    }
}
