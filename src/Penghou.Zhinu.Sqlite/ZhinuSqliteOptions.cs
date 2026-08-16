namespace Penghou.Zhinu.Sqlite;

/// <summary>Configures local SQLite workflow persistence.</summary>
public sealed class ZhinuSqliteOptions
{
    public required string DatabasePath { get; set; }

    public bool EnableWal { get; set; } = true;

    public TimeSpan BusyTimeout { get; set; } = TimeSpan.FromSeconds(5);
}
