namespace Penghou.Zhinu.Context;

/// <summary>
/// Owns the per-step-key reference-counted semaphores used to serialize
/// concurrent executions of the same durable step within one run.
/// </summary>
internal sealed class StepLockManager
{
    private readonly object lockSync = new();
    private readonly Dictionary<string, RefCountedSemaphore> stepLocks = new(StringComparer.Ordinal);

    public async Task<IDisposable> AcquireAsync(
        string stepKey,
        CancellationToken cancellationToken)
    {
        RefCountedSemaphore refSem;
        lock (lockSync)
        {
            if (stepLocks.TryGetValue(stepKey, out var existing))
            {
                existing.RefCount++;
                refSem = existing;
            }
            else
            {
                refSem = new RefCountedSemaphore();
                stepLocks[stepKey] = refSem;
            }
        }
        try
        {
            await refSem.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new StepLockLease(this, stepKey, refSem);
        }
        catch
        {
            lock (lockSync)
            {
                refSem.RefCount--;
                if (refSem.RefCount == 0)
                {
                    stepLocks.Remove(stepKey);
                    refSem.Semaphore.Dispose();
                }
            }
            throw;
        }
    }

    private void Release(string stepKey, RefCountedSemaphore refSem)
    {
        refSem.Semaphore.Release();
        lock (lockSync)
        {
            refSem.RefCount--;
            if (refSem.RefCount == 0)
            {
                stepLocks.Remove(stepKey);
                refSem.Semaphore.Dispose();
            }
        }
    }

    private sealed class StepLockLease : IDisposable
    {
        private readonly StepLockManager owner;
        private readonly string stepKey;
        private readonly RefCountedSemaphore refSem;
        private bool disposed;

        public StepLockLease(
            StepLockManager owner,
            string stepKey,
            RefCountedSemaphore refSem)
        {
            this.owner = owner;
            this.stepKey = stepKey;
            this.refSem = refSem;
        }

        public void Dispose()
        {
            if (disposed)
                return;
            disposed = true;
            owner.Release(stepKey, refSem);
        }
    }

    private sealed class RefCountedSemaphore
    {
        public readonly SemaphoreSlim Semaphore = new(1, 1);
        public int RefCount = 1;
    }
}
