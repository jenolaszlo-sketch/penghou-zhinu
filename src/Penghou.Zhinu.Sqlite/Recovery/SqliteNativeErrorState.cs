namespace Penghou.Zhinu.Sqlite.Recovery;

/// <summary>Native SQLite error state read from the failing connection before disposal.</summary>
internal readonly record struct SqliteNativeErrorState(
    int PrimaryCode,
    int ExtendedCode,
    int SystemErrno);
