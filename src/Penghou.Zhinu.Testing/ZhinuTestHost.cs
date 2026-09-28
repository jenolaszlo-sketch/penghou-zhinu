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
        Clock = TimeProvider as TestTimeProvider;
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

    /// <summary>The controllable clock when one was supplied, otherwise null.</summary>
    public TestTimeProvider? Clock { get; }

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
    /// Returns when the run has no immediately runnable work, or is terminal.
    /// The returned waits are the precise parked reason; pair this with
    /// <see cref="AdvanceToNextDurableTimerAsync"/> to progress deterministically.
    /// </summary>
    public Task<BlockedRun> RunUntilBlockedAsync(
        Guid workflowRunId,
        CancellationToken cancellationToken = default) =>
        Engine.WaitUntilBlockedAsync(workflowRunId, cancellationToken);

    /// <summary>
    /// Advances the supplied <see cref="TestTimeProvider"/> to the next parked
    /// durable wait that is due in the future (retry backoff, signal deadline,
    /// or child availability), firing any timers along the way. Returns the
    /// amount advanced, or null when no future durable timer is parked.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// No <see cref="TestTimeProvider"/> was supplied to the host.
    /// </exception>
    public async Task<TimeSpan?> AdvanceToNextDurableTimerAsync(
        Guid workflowRunId,
        CancellationToken cancellationToken = default)
    {
        if (Clock is null)
        {
            throw new InvalidOperationException(
                "AdvanceToNextDurableTimerAsync requires a TestTimeProvider.");
        }
        var waits = await Store.ListWaitsAsync(workflowRunId, cancellationToken)
            .ConfigureAwait(false);
        var now = Clock.GetUtcNow();
        DateTimeOffset? next = null;
        foreach (var wait in waits)
        {
            if (wait.Status != WaitStatus.Parked)
                continue;
            var candidate = wait.AvailableAt ?? wait.DeadlineAt;
            if (candidate is null || candidate <= now)
                continue;
            if (next is null || candidate < next)
                next = candidate;
        }
        if (next is null)
            return null;
        var delta = next.Value - now;
        Clock.Advance(delta);
        return delta;
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
