using Microsoft.Data.Sqlite;

namespace Penghou.Zhinu.Sqlite.Recovery;

/// <summary>
/// Internal capability used only by mapped-section recovery. A fresh open must
/// not silently reacquire a pooled physical connection that observed the native
/// failure, so the retry opens with pooling disabled for that attempt.
/// </summary>
internal interface IZhinuSqliteDatabaseRecovery
{
    ValueTask<SqliteConnection> OpenFreshAsync(CancellationToken cancellationToken);
}
