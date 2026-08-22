using Microsoft.Data.Sqlite;
using Penghou.Zhinu.Testing;

namespace Penghou.Zhinu.Sqlite.Tests;

/// <summary>A conformance fixture backing two independent stores with one SQLite file.</summary>
internal sealed class SqliteWorkflowStoreFixture : IWorkflowStoreFixture
{
    private readonly string path;
    private readonly string directory;

    public SqliteWorkflowStoreFixture()
    {
        directory = Path.Combine(Path.GetTempPath(), "penghou-zhinu-conformance", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        path = Path.Combine(directory, "zhinu.db");
    }

    public IWorkflowStore Store => CreateStore();

    public IWorkflowStore CreatePeerStore() => CreateStore();

    public TimeProvider TimeProvider => TimeProvider.System;

    private SqliteWorkflowStore CreateStore() =>
        new(new ZhinuSqliteOptions
        {
            DatabasePath = path,
            BusyTimeout = TimeSpan.FromSeconds(2),
            Pooling = false
        });

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
        return ValueTask.CompletedTask;
    }
}
