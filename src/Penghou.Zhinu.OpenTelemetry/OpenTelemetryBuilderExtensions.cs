using OpenTelemetry;
using Penghou.Zhinu.Sqlite;

namespace Penghou.Zhinu.OpenTelemetry;

/// <summary>Registers Zhinu diagnostic sources with OpenTelemetry.</summary>
public static class OpenTelemetryBuilderExtensions
{
    /// <summary>
    /// Adds Zhinu core and SQLite tracing and metrics. Exporters remain the
    /// application's responsibility.
    /// </summary>
    public static OpenTelemetryBuilder AddZhinuInstrumentation(
        this OpenTelemetryBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder
            .WithTracing(tracing => tracing.AddSource(
                ZhinuDiagnostics.ActivitySourceName,
                ZhinuSqliteDiagnostics.ActivitySourceName))
            .WithMetrics(metrics => metrics.AddMeter(
                ZhinuDiagnostics.MeterName,
                ZhinuSqliteDiagnostics.MeterName));
    }
}
