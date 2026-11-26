using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Penghou.Zhinu.Sqlite.Diagnostics;

/// <summary>
/// REL-SQLITE-01 diagnostic-only native error probe.
/// Inert unless ZG_DIAG_SQLITE_ERROR_LOG is set. When enabled it reads the
/// failing connection's native error state immediately after a SqliteException,
/// before the connection is disposed and before any subsequent SQLite call,
/// and appends one JSON record per process. It never issues SQL, never retries
/// and never changes exception flow. This file must not ship in a release
/// package.
/// </summary>
internal static class RelSqlite01ErrorProbe
{
    private static readonly object Gate = new();
    private static readonly Lazy<SystemErrnoFunction?> SystemErrno = new(ResolveSystemErrno);

    public static void Capture(SqliteConnection connection, string operation, SqliteException exception)
    {
        var logPath = Environment.GetEnvironmentVariable("ZG_DIAG_SQLITE_ERROR_LOG");
        if (string.IsNullOrWhiteSpace(logPath))
            return;

        try
        {
            int? nativePrimary = null;
            int? nativeExtended = null;
            int? nativeSystemErrno = null;
            string? systemErrnoSource = null;

            var handle = connection.Handle;
            if (handle is not null && !handle.IsInvalid)
            {
                nativePrimary = SQLitePCL.raw.sqlite3_errcode(handle);
                nativeExtended = SQLitePCL.raw.sqlite3_extended_errcode(handle);
                var function = SystemErrno.Value;
                if (function is not null)
                {
                    try
                    {
                        nativeSystemErrno = function(handle.DangerousGetHandle());
                        systemErrnoSource = "sqlite3_system_errno";
                    }
                    catch
                    {
                        systemErrnoSource = "sqlite3_system_errno threw";
                    }
                }
                else
                {
                    systemErrnoSource = "export unavailable";
                }
            }
            else
            {
                systemErrnoSource = "connection handle unavailable";
            }

            var record = new
            {
                timestampUtc = DateTimeOffset.UtcNow.ToString("o"),
                monotonicMs = Environment.TickCount64,
                processId = Environment.ProcessId,
                threadId = Environment.CurrentManagedThreadId,
                operation,
                framework = RuntimeInformation.FrameworkDescription,
                database = TryRead(() => connection.DataSource),
                connectionState = TryRead(() => connection.State.ToString()),
                managedException = exception.GetType().FullName,
                managedMessage = exception.Message,
                managedPrimary = exception.SqliteErrorCode,
                managedExtended = exception.SqliteExtendedErrorCode,
                nativePrimary,
                nativeExtended,
                nativeSystemErrno,
                systemErrnoSource
            };

            var line = JsonSerializer.Serialize(record);
            var destination = logPath + "." + Environment.ProcessId + ".jsonl";
            var directory = Path.GetDirectoryName(Path.GetFullPath(destination));
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);
            lock (Gate)
                File.AppendAllText(destination, line + Environment.NewLine);
        }
        catch
        {
            // Diagnostic-only path: a probe failure must never replace the
            // original persistence exception or alter recovery behaviour.
        }
    }

    private static T? TryRead<T>(Func<T> read)
    {
        try
        {
            return read();
        }
        catch
        {
            return default;
        }
    }

    private delegate int SystemErrnoFunction(IntPtr database);

    private static SystemErrnoFunction? ResolveSystemErrno()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var module = GetModuleHandleW("e_sqlite3.dll");
                if (module == IntPtr.Zero)
                    return null;
                var address = NativeLibrary.GetExport(module, "sqlite3_system_errno");
                return Marshal.GetDelegateForFunctionPointer<SystemErrnoFunction>(address);
            }

            if (!NativeLibrary.TryLoad("libe_sqlite3.so", out var unixModule) ||
                !NativeLibrary.TryGetExport(unixModule, "sqlite3_system_errno", out var unixAddress))
                return null;
            return Marshal.GetDelegateForFunctionPointer<SystemErrnoFunction>(unixAddress);
        }
        catch
        {
            return null;
        }
    }

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandleW(string moduleName);
}
