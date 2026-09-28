using Penghou.Zhinu.Sqlite;

namespace Penghou.Zhinu.Testing;

/// <summary>Owns an isolated temporary SQLite store and workflow engine.</summary>
public sealed class ZhinuTestHost : IAsyncDisposable
{
    private readonly string directory;

    public ZhinuTestHost(
        WorkflowRegistry registry,
        Action<ZhinuOptions>? configure = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        TimeProvider = timeProvider ?? TimeProvider.System;
        directory = Path.Combine(Path.GetTempPath(), "penghou-zhinu", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        Store = new SqliteWorkflowStore(new ZhinuSqliteOptions
        {
            DatabasePath = Path.Combine(directory, "zhinu.db"),
            TimeProvider = TimeProvider
        });
        var options = new ZhinuOptions { PollInterval = TimeSpan.FromMilliseconds(10) };
        configure?.Invoke(options);
        Engine = new WorkflowEngine(Store, registry, options, null, TimeProvider, null, null, null);
    }

    public TimeProvider TimeProvider { get; }

    public WorkflowEngine Engine { get; }
    public SqliteWorkflowStore Store { get; }

    /// <summary>Runs available work repeatedly until no run is immediately runnable.</summary>
    public async Task RunUntilIdleAsync(CancellationToken cancellationToken = default)
    {
        while (await Engine.RunAvailableAsync(cancellationToken).ConfigureAwait(false) > 0)
        {
        }
    }

    /// <summary>
    /// Waits until an observable state holds or the timeout elapses. The timeout
    /// is measured on the configured clock.
    /// </summary>
    public async Task WaitForStateAsync(
        Func<CancellationToken, ValueTask<bool>> condition,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        using var timeoutSource = new CancellationTokenSource(timeout, TimeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeoutSource.Token);
        while (!await condition(linked.Token).ConfigureAwait(false))
            await Task.Delay(TimeSpan.FromMilliseconds(10), TimeProvider, linked.Token)
                .ConfigureAwait(false);
    }

    /// <summary>
    /// Simulates process loss: drops the engine without graceful shutdown so
    /// recovery paths observe abandoned leases and state. Returns a fresh
    /// engine over the same store; persisted state is untouched.
    /// </summary>
    public WorkflowEngine CrashAndReopen(WorkflowRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return new WorkflowEngine(
            Store,
            registry,
            new ZhinuOptions { PollInterval = TimeSpan.FromMilliseconds(10) },
            null,
            TimeProvider,
            null,
            null,
            null);
    }

    public async ValueTask DisposeAsync()
    {
        await Engine.DisposeAsync().ConfigureAwait(false);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}
