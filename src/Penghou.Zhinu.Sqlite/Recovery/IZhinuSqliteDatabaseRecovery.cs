using Microsoft.Data.Sqlite;

namespace Penghou.Zhinu.Sqlite.Recovery;

/// <summary>
/// Internal capability used only by mapped-section recovery. Every attempt of an
/// eligible Windows recovery operation opens a recovery-owned, non-pooled
/// physical connection. A failed attempt is therefore closed physically when it
/// is disposed: it is not returned to a pool, and neither a retry nor a later
/// ordinary open can silently reacquire it.
/// </summary>
internal interface IZhinuSqliteDatabaseRecovery
{
    ValueTask<SqliteConnection> OpenFreshAsync(CancellationToken cancellationToken);
}
