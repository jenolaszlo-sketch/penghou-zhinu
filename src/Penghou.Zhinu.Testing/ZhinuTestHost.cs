using Penghou.Zhinu.Sqlite;

namespace Penghou.Zhinu.Testing;

/// <summary>Owns an isolated temporary SQLite store and workflow engine.</summary>
public sealed class ZhinuTestHost : IAsyncDisposable
{
    private readonly string directory;

    public ZhinuTestHost(
        WorkflowRegistry registry,
        Action<ZhinuOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        directory = Path.Combine(Path.GetTempPath(), "penghou-zhinu", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        Store = new SqliteWorkflowStore(new ZhinuSqliteOptions
        {
            DatabasePath = Path.Combine(directory, "zhinu.db")
        });
        var options = new ZhinuOptions { PollInterval = TimeSpan.FromMilliseconds(10) };
        configure?.Invoke(options);
        Engine = new WorkflowEngine(Store, registry, options);
    }

    public WorkflowEngine Engine { get; }
    public SqliteWorkflowStore Store { get; }

    /// <summary>Runs available work repeatedly until no run is immediately runnable.</summary>
    public async Task RunUntilIdleAsync(CancellationToken cancellationToken = default)
    {
        while (await Engine.RunAvailableAsync(cancellationToken).ConfigureAwait(false) > 0)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Engine.DisposeAsync().ConfigureAwait(false);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}
