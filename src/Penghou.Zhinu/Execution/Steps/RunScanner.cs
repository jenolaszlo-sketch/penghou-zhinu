namespace Penghou.Zhinu.Execution.Steps;

/// <summary>
/// Recovers expired leases when due and then executes every currently
/// runnable run, bounding the number of workflows executed concurrently.
/// </summary>
internal sealed class RunScanner
{
    private readonly IWorkflowStore store;
    private readonly ZhinuOptions options;
    private readonly TimeProvider timeProvider;
    private readonly LeaseRecoveryScheduler leaseRecovery;
    private readonly Func<Guid, CancellationToken, Task> executeRun;

    public RunScanner(
        IWorkflowStore store,
        ZhinuOptions options,
        TimeProvider timeProvider,
        LeaseRecoveryScheduler leaseRecovery,
        Func<Guid, CancellationToken, Task> executeRun)
    {
        this.store = store;
        this.options = options;
        this.timeProvider = timeProvider;
        this.leaseRecovery = leaseRecovery;
        this.executeRun = executeRun;
    }

    public async Task<int> RunAvailableAsync(
        CancellationToken cancellationToken = default)
    {
        await leaseRecovery.RecoverExpiredLeasesIfDueAsync(cancellationToken)
            .ConfigureAwait(false);
        var ids = await store.GetRunnableRunIdsAsync(
            timeProvider.GetUtcNow(),
            options.ScanBatchSize,
            cancellationToken).ConfigureAwait(false);
        using var concurrency = new SemaphoreSlim(
            options.MaxConcurrentWorkflows,
            options.MaxConcurrentWorkflows);
        await Task.WhenAll(ids.Select(async id =>
        {
            await concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await executeRun(id, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                concurrency.Release();
            }
        })).ConfigureAwait(false);
        return ids.Count;
    }
}
