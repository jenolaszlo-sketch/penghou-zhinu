namespace Penghou.Zhinu.Sqlite;

/// <summary>
/// Thrown when an existing SQLite database was created by an incompatible or
/// unversioned Zhinu schema. Preview releases do not migrate schemas.
/// </summary>
public sealed class ZhinuSchemaCompatibilityException : Exception
{
    public ZhinuSchemaCompatibilityException(int expectedVersion, int? databaseVersion)
        : base(databaseVersion is null
            ? $"The SQLite database has an unversioned Zhinu schema; runtime schema " +
              $"{expectedVersion} is required. Recreate the preview database."
            : $"SQLite database schema {databaseVersion} is incompatible with runtime " +
              $"schema {expectedVersion}. Recreate the preview database.")
    {
        ExpectedVersion = expectedVersion;
        DatabaseVersion = databaseVersion;
    }

    public int ExpectedVersion { get; }

    public int? DatabaseVersion { get; }
}

/// <summary>Exposes the schema expected by this SQLite package version.</summary>
public static class ZhinuSqliteSchema
{
    public const int CurrentVersion = 4;
}
