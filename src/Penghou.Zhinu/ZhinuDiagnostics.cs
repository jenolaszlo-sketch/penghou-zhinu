using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Penghou.Zhinu;

/// <summary>Stable diagnostics names for OpenTelemetry configuration.</summary>
public static class ZhinuDiagnostics
{
    public const string ActivitySourceName = "Penghou.Zhinu";
    public const string MeterName = "Penghou.Zhinu";

    internal static readonly ActivitySource Activities = new(ActivitySourceName);
    internal static readonly Meter Meter = new(MeterName);
    internal static readonly Counter<long> RunsStarted = Meter.CreateCounter<long>("zhinu.runs.started");
    internal static readonly Counter<long> RunsCompleted = Meter.CreateCounter<long>("zhinu.runs.completed");
    internal static readonly Counter<long> RunsFailed = Meter.CreateCounter<long>("zhinu.runs.failed");
    internal static readonly Histogram<double> RunDuration =
        Meter.CreateHistogram<double>("zhinu.runs.duration", "s");
}
