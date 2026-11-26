using Microsoft.Data.Sqlite;

namespace Penghou.Zhinu.Sqlite.Recovery;

/// <summary>
/// Reads the native error state of a failing connection. The production
/// implementation uses the connection's own SQLite handle and the already
/// loaded native library; it never loads an independent SQLite implementation.
/// </summary>
internal interface ISqliteNativeErrorStateReader
{
    bool TryRead(SqliteConnection connection, out SqliteNativeErrorState state);
}
