using Microsoft.Data.Sqlite;

namespace Penghou.Zhinu.Agents.Tests;

/// <summary>
/// Removes a test's temporary directory after releasing pooled and finalizable
/// SQLite handles. On Windows a connection can still hold the database file for
/// a short window after a crash/restart test, so the delete retries with a
/// bounded backoff instead of failing the run.
/// </summary>
internal static class TestDirectory
{
    public static void DeleteResilient(string path)
    {
        for (var attempt = 1; Directory.Exists(path); attempt++)
        {
            SqliteConnection.ClearAllPools();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 10)
            {
                Thread.Sleep(50 * attempt);
            }
        }
    }
}
