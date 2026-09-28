namespace Penghou.Zhinu;

/// <summary>
/// Renews a lease on an interval until disposed. A rejected renewal (confirmed
/// loss) notifies the owner so it can cooperatively cancel; transient renewal
/// errors stop this loop without cancelling, since fencing stays authoritative
/// at commit time and the next interval may succeed.
/// </summary>
internal sealed class LeaseRenewal : IAsyncDisposable
{
    private readonly CancellationTokenSource cancellation = new();
    private readonly Task renewalTask;

    public LeaseRenewal(
        TimeProvider timeProvider,
        TimeSpan interval,
        Func<CancellationToken, ValueTask<bool>> renew,
        Func<CancellationToken, ValueTask>? onLeaseLost = null)
    {
        renewalTask = RunAsync(timeProvider, interval, renew, onLeaseLost, cancellation.Token);
    }

    public async ValueTask DisposeAsync()
    {
        await cancellation.CancelAsync().ConfigureAwait(false);
        try
        {
            await renewalTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        cancellation.Dispose();
    }

    private static async Task RunAsync(
        TimeProvider timeProvider,
        TimeSpan interval,
        Func<CancellationToken, ValueTask<bool>> renew,
        Func<CancellationToken, ValueTask>? onLeaseLost,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            await Task.Delay(interval, timeProvider, cancellationToken)
                .ConfigureAwait(false);
            bool renewed;
            try
            {
                renewed = await renew(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                // Transient renewal error: keep the loop alive so a passing
                // fault does not silently drop protection. Fencing stays
                // authoritative at commit time.
                continue;
            }
            if (renewed)
                continue;
            if (onLeaseLost is not null)
                await onLeaseLost(cancellationToken).ConfigureAwait(false);
            return;
        }
    }
}
