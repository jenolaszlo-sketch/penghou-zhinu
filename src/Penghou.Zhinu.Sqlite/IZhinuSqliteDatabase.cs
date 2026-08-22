using Microsoft.Data.Sqlite;

namespace Penghou.Zhinu.Sqlite;

/// <summary>
/// Owns one SQLite database file: idempotent initialization, schema
/// compatibility, PRAGMA configuration, connection creation, and explicit
/// maintenance. All components that persist to the same database path must
/// share a single <see cref="IZhinuSqliteDatabase"/> so initialization and
/// PRAGMAs stay consistent and cannot race.
/// </summary>
public interface IZhinuSqliteDatabase
{
    ZhinuSqliteOptions Options { get; }

    TimeProvider TimeProvider { get; }

    /// <summary>Runs idempotent schema initialization and compatibility checks exactly once per database owner.</summary>
    ValueTask InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>Initializes when not yet initialized; safe to call concurrently.</summary>
    ValueTask EnsureInitializedAsync(CancellationToken cancellationToken = default);

    /// <summary>Opens a connection with foreign keys and busy-timeout PRAGMAs applied.</summary>
    ValueTask<SqliteConnection> OpenAsync(CancellationToken cancellationToken = default);

    /// <summary>Truncates the WAL file via a checkpoint.</summary>
    ValueTask CheckpointAsync(CancellationToken cancellationToken = default);

    /// <summary>Explicitly reclaims free pages. Not run automatically; call infrequently and off hot paths.</summary>
    ValueTask VacuumAsync(CancellationToken cancellationToken = default);
}
