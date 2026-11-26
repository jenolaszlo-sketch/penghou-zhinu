using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;

namespace Penghou.Zhinu.Sqlite.Recovery;

internal sealed class SqliteRecoveryRuntime
{
    public SqliteRecoveryRuntime(
        ISqliteNativeErrorStateReader nativeErrorStateReader,
        Func<TimeSpan, CancellationToken, ValueTask> delay,
        TimeProvider timeProvider,
        bool isWindows,
        Func<SqliteRecoveryOperation, int, Exception?>? attemptFailureInjector = null,
        Action<SqliteConnection, int>? connectionOpened = null)
    {
        NativeErrorStateReader = nativeErrorStateReader
            ?? throw new ArgumentNullException(nameof(nativeErrorStateReader));
        Delay = delay ?? throw new ArgumentNullException(nameof(delay));
        TimeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        IsWindows = isWindows;
        AttemptFailureInjector = attemptFailureInjector;
        ConnectionOpened = connectionOpened;
    }

    public ISqliteNativeErrorStateReader NativeErrorStateReader { get; }

    public Func<TimeSpan, CancellationToken, ValueTask> Delay { get; }

    public TimeProvider TimeProvider { get; }

    public bool IsWindows { get; }

    public Func<SqliteRecoveryOperation, int, Exception?>? AttemptFailureInjector { get; }

    public Action<SqliteConnection, int>? ConnectionOpened { get; }

    public static SqliteRecoveryRuntime Default { get; } = new(
        new SqliteNativeErrorStateReader(),
        static (delay, cancellationToken) => new ValueTask(Task.Delay(delay, cancellationToken)),
        TimeProvider.System,
        OperatingSystem.IsWindows());
}

internal static class SqliteRecoveryTestHooks
{
    internal static readonly AsyncLocal<SqliteRecoveryRuntime?> Runtime = new();
}
