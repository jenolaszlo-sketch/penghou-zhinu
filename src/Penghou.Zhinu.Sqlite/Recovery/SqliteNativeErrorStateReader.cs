using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;

namespace Penghou.Zhinu.Sqlite.Recovery;

/// <summary>
/// Reads primary, extended and Windows system error codes directly from the
/// failing connection's SQLite handle. Any failure to read yields
/// <see langword="false"/>; the caller then refuses to retry and preserves the
/// original exception.
/// </summary>
internal sealed class SqliteNativeErrorStateReader : ISqliteNativeErrorStateReader
{
    private static readonly Lazy<SystemErrnoFunction?> SystemErrno = new(ResolveSystemErrno);

    public bool TryRead(SqliteConnection connection, out SqliteNativeErrorState state)
    {
        state = default;
        try
        {
            var handle = connection.Handle;
            if (handle is null || handle.IsInvalid)
                return false;

            var primary = SQLitePCL.raw.sqlite3_errcode(handle);
            var extended = SQLitePCL.raw.sqlite3_extended_errcode(handle);
            var systemErrno = SystemErrno.Value;
            if (systemErrno is null)
                return false;

            state = new SqliteNativeErrorState(
                primary,
                extended,
                systemErrno(handle.DangerousGetHandle()));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private delegate int SystemErrnoFunction(IntPtr database);

    private static SystemErrnoFunction? ResolveSystemErrno()
    {
        try
        {
            if (!OperatingSystem.IsWindows())
                return null;

            // Resolve from the module SQLitePCLRaw already loaded. Do not load
            // a second copy of the native library.
            var module = GetModuleHandleW("e_sqlite3.dll");
            if (module == IntPtr.Zero)
                return null;

            var address = NativeLibrary.GetExport(module, "sqlite3_system_errno");
            return Marshal.GetDelegateForFunctionPointer<SystemErrnoFunction>(address);
        }
        catch
        {
            return null;
        }
    }

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandleW(string moduleName);
}
