namespace Penghou.Zhinu.Execution.Steps;

/// <summary>
/// Coordinates one-time store initialization and periodic recovery of
/// expired leases, keeping the engine's own state tracking in one place.
/// </summary>
internal sealed class LeaseRecoveryScheduler
{
    private readonly IWorkflowStore store;
    private readonly ZhinuOptions options;
    private readonly TimeProvider timeProvider;
    private readonly SemaphoreSlim initializationLock = new(1, 1);
    private DateTimeOffset lastLeaseRecovery;
    private volatile bool initialized;

    public LeaseRecoveryScheduler(
        IWorkflowStore store,
        ZhinuOptions options,
        TimeProvider timeProvider)
    {
        this.store = store;
        this.options = options;
        this.timeProvider = timeProvider;
    }

    public async ValueTask RecoverExpiredLeasesIfDueAsync(
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var nextRecovery = lastLeaseRecovery + options.LeaseRecoveryInterval;
        if (now < nextRecovery)
            return;
        lastLeaseRecovery = now;
        using var activity = ZhinuDiagnostics.StartActivity(
            ZhinuDiagnostics.Activities.LeaseRecover);
        var recovered = await store.RecoverExpiredLeasesAsync(
            now,
            cancellationToken).ConfigureAwait(false);
        if (recovered > 0)
            ZhinuDiagnostics.LeasesRecoveredCounter.Add(recovered);
    }

    public async ValueTask EnsureInitializedAsync(
        CancellationToken cancellationToken)
    {
        if (initialized)
            return;
        await initializationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (initialized)
                return;
            await store.InitializeAsync(cancellationToken).ConfigureAwait(false);
            var recovered = await store.RecoverExpiredLeasesAsync(
                timeProvider.GetUtcNow(),
                cancellationToken).ConfigureAwait(false);
            if (recovered > 0)
                ZhinuDiagnostics.LeasesRecoveredCounter.Add(recovered);
            lastLeaseRecovery = timeProvider.GetUtcNow();
            initialized = true;
        }
        finally
        {
            initializationLock.Release();
        }
    }
}
