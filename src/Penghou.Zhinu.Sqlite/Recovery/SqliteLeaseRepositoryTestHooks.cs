namespace Penghou.Zhinu.Sqlite.Recovery;

/// <summary>
/// Test-only seam proving that a matching native failure after a successful
/// <c>BEGIN IMMEDIATE</c> cannot re-enter the retry loop. Null in production.
/// </summary>
internal static class SqliteLeaseRepositoryTestHooks
{
    internal static readonly AsyncLocal<Func<Exception?>?> AfterBegin = new();
}
