using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Penghou.Zhinu.Sqlite;

/// <summary>Stable diagnostics conventions emitted by the SQLite provider.</summary>
public static class ZhinuSqliteDiagnostics
{
    public const string ActivitySourceName = "Penghou.Zhinu.Sqlite";
    public const string MeterName = "Penghou.Zhinu.Sqlite";
    public const string ConnectionOpenActivity = "zhinu.sqlite.connection.open";
    public const string InitializeActivity = "zhinu.sqlite.initialize";
    public const string ConnectionOpenDuration = "zhinu.sqlite.connection.open.duration";
    public const string FailureCount = "zhinu.sqlite.store.failures";
    public const string BusyCount = "zhinu.sqlite.store.busy";
    public const string ConnectionFailureCount = "zhinu.sqlite.connection.failures";
    public const string ConnectionBusyCount = "zhinu.sqlite.connection.busy";
    public const string StoreOperationActivity = "zhinu.sqlite.store.operation";
    public const string StoreOperationDuration = "zhinu.sqlite.store.operation.duration";
    public const string StoreOperationName = "zhinu.sqlite.operation";

    internal static readonly ActivitySource ActivitySource = new(ActivitySourceName);
    internal static readonly Meter Meter = new(MeterName);
    internal static readonly Histogram<double> OpenDuration =
        Meter.CreateHistogram<double>(ConnectionOpenDuration, "s");
    internal static readonly Histogram<double> OperationDuration =
        Meter.CreateHistogram<double>(StoreOperationDuration, "s");
    internal static readonly Counter<long> Failures = Meter.CreateCounter<long>(FailureCount);
    internal static readonly Counter<long> Busy = Meter.CreateCounter<long>(BusyCount);
    internal static readonly Counter<long> ConnectionFailures =
        Meter.CreateCounter<long>(ConnectionFailureCount);
    internal static readonly Counter<long> ConnectionBusy =
        Meter.CreateCounter<long>(ConnectionBusyCount);
}
