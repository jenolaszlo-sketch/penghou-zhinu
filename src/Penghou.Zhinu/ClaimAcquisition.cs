namespace Penghou.Zhinu;

/// <summary>
/// Shared step-claim acquisition loop. Polls <see cref="StepClaimDisposition.Deferred"/>
/// and <see cref="StepClaimDisposition.Busy"/>, delegates
/// <see cref="StepClaimDisposition.Waiting"/> to the caller, and maps terminal
/// dispositions through the caller. Returns only acquired or reused results.
/// Delay and signal paths keep bespoke handling because their waiting arms
/// complete or deliver instead of re-claiming.
/// </summary>
internal static class ClaimAcquisition
{
    public static async ValueTask<StepClaimResult> AcquireAsync(
        Func<CancellationToken, ValueTask<StepClaimResult>> claim,
        Func<StepClaimResult, CancellationToken, Task> waitAsync,
        Func<StepClaimResult, Exception> terminal,
        TimeProvider timeProvider,
        TimeSpan pollInterval,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var result = await claim(cancellationToken).ConfigureAwait(false);
            switch (result.Disposition)
            {
                case StepClaimDisposition.Acquired:
                case StepClaimDisposition.Reused:
                    return result;
                case StepClaimDisposition.Deferred:
                case StepClaimDisposition.Busy:
                    await Task.Delay(pollInterval, timeProvider, cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                case StepClaimDisposition.Waiting:
                    await waitAsync(result, cancellationToken).ConfigureAwait(false);
                    continue;
                default:
                    throw terminal(result);
            }
        }
    }
}
