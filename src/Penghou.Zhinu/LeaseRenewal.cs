namespace Penghou.Zhinu;

internal sealed class LeaseRenewal : IAsyncDisposable
{
    private readonly CancellationTokenSource cancellation = new();
    private readonly Task renewalTask;

    public LeaseRenewal(
        TimeProvider timeProvider,
        TimeSpan interval,
        Func<CancellationToken, ValueTask<bool>> renew)
    {
        renewalTask = RunAsync(timeProvider, interval, renew, cancellation.Token);
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
        CancellationToken cancellationToken)
    {
        while (true)
        {
            await Task.Delay(interval, timeProvider, cancellationToken)
                .ConfigureAwait(false);
            if (!await renew(cancellationToken).ConfigureAwait(false))
                return;
        }
    }
}
