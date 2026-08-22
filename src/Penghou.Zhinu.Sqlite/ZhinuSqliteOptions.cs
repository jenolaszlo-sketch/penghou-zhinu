namespace Penghou.Zhinu.Sqlite;

/// <summary>Configures local SQLite workflow persistence.</summary>
public sealed class ZhinuSqliteOptions
{
    public required string DatabasePath { get; set; }

    public bool EnableWal { get; set; } = true;

    public TimeSpan BusyTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Emits connection and initialization spans. Metrics remain available to
    /// listeners regardless of this setting. SQL and payloads are never added.
    /// </summary>
    public bool EnableDetailedDiagnostics { get; set; }

    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;

    internal ZhinuSqliteOptions Clone() => new()
    {
        DatabasePath = DatabasePath,
        EnableWal = EnableWal,
        BusyTimeout = BusyTimeout,
        EnableDetailedDiagnostics = EnableDetailedDiagnostics,
        TimeProvider = TimeProvider
    };

    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(DatabasePath);
        if (BusyTimeout < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(BusyTimeout));
    }
}
