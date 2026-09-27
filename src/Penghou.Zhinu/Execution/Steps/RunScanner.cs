using Microsoft.Extensions.Logging;

namespace Penghou.Zhinu.Execution.Steps;

/// <summary>
/// Recovers expired leases when due and admits runnable runs under a shared
/// concurrency budget. Direct <c>RunAvailableAsync</c> calls keep their
/// blocking semantics (admitted work completes before return); hosted loops
/// use <c>AdmitAvailableAsync</c> to fill free slots without waiting for the
/// slowest execution, and drain owned work at shutdown.
/// </summary>
internal sealed class RunScanner
{
    private readonly IWorkflowStore store;
    private readonly ZhinuOptions options;
    private readonly TimeProvider timeProvider;
    private readonly LeaseRecoveryScheduler leaseRecovery;
    private readonly Func<Guid, CancellationToken, Task> executeRun;
    private readonly ILogger logger;
    private readonly SemaphoreSlim budget;
    private readonly Dictionary<Guid, Task> owned = new();
    private readonly object ownedLock = new();

    public RunScanner(
        IWorkflowStore store,
        ZhinuOptions options,
        TimeProvider timeProvider,
        LeaseRecoveryScheduler leaseRecovery,
        Func<Guid, CancellationToken, Task> executeRun,
        ILogger logger)
    {
        this.store = store;
        this.options = options;
        this.timeProvider = timeProvider;
        this.leaseRecovery = leaseRecovery;
        this.executeRun = executeRun;
        this.logger = logger;
        budget = new SemaphoreSlim(options.MaxConcurrentWorkflows, options.MaxConcurrentWorkflows);
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
        var batch = new List<Task>();
        foreach (var id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (ownedLock)
            {
                if (owned.ContainsKey(id))
                    continue;
            }
            await budget.WaitAsync(cancellationToken).ConfigureAwait(false);
            var admitted = TrackExecutionAsync(id, cancellationToken);
            lock (ownedLock)
            {
                owned[id] = admitted;
            }
            batch.Add(admitted);
        }
        await Task.WhenAll(batch).ConfigureAwait(false);
        return batch.Count;
    }

    public async Task<int> AdmitAvailableAsync(
        CancellationToken cancellationToken = default)
    {
        await leaseRecovery.RecoverExpiredLeasesIfDueAsync(cancellationToken)
            .ConfigureAwait(false);
        PruneOwned();
        var ids = await store.GetRunnableRunIdsAsync(
            timeProvider.GetUtcNow(),
            options.ScanBatchSize,
            cancellationToken).ConfigureAwait(false);
        var admitted = 0;
        foreach (var id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (ownedLock)
            {
                if (owned.ContainsKey(id))
                    continue;
            }
            if (!await budget.WaitAsync(TimeSpan.Zero, cancellationToken).ConfigureAwait(false))
                break;
            var execution = TrackExecutionAsync(id, cancellationToken);
            lock (ownedLock)
            {
                owned[id] = execution;
            }
            _ = execution.ContinueWith(
                static (task, state) =>
                {
                    var (log, runId) = ((ILogger, Guid))state!;
                    if (task.IsFaulted)
                        log.LogError(
                            task.Exception,
                            "Admitted execution {WorkflowRunId} failed.",
                            runId);
                },
                (logger, id),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
            admitted++;
        }
        return admitted;
    }

    public async Task DrainAdmittedAsync(CancellationToken cancellationToken = default)
    {
        Task[] snapshot;
        lock (ownedLock)
        {
            PruneOwned();
            snapshot = new Task[owned.Count];
            owned.Values.CopyTo(snapshot, 0);
        }
        if (snapshot.Length == 0)
            return;
        await Task.WhenAll(snapshot).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private void PruneOwned()
    {
        lock (ownedLock)
        {
            foreach (var id in new List<Guid>(owned.Keys))
            {
                if (owned[id].IsCompleted)
                    owned.Remove(id);
            }
        }
    }

    private async Task TrackExecutionAsync(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            await executeRun(id, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            budget.Release();
        }
    }
}
