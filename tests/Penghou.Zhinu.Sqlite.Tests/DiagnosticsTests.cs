using FluentAssertions;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Penghou.Zhinu.Sqlite.Tests;

public sealed class DiagnosticsTests : WorkflowEngineTestBase
{
    [Fact]
    public async Task Execution_EmitsStableActivitiesMetricsAndDurableTraceId()
    {
        var activities = new ConcurrentBag<Activity>();
        using var activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == ZhinuDiagnostics.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activities.Add
        };
        ActivitySource.AddActivityListener(activityListener);

        var measurements = new ConcurrentBag<string>();
        using var meterListener = new MeterListener();
        meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == ZhinuDiagnostics.MeterName)
                listener.EnableMeasurementEvents(instrument);
        };
        meterListener.SetMeasurementEventCallback<long>(
            (instrument, _, _, _) => measurements.Add(instrument.Name));
        meterListener.SetMeasurementEventCallback<double>(
            (instrument, _, _, _) => measurements.Add(instrument.Name));
        meterListener.Start();

        var creatingEngine = CreateEngine(new ObservedWorkflow(), "observed");
        var runId = await creatingEngine.StartAsync(
            "observed", "1", "hello",
            cancellationToken: TestContext.Current.CancellationToken);
        var resumingEngine = CreateEngine(new ObservedWorkflow(), "observed");
        await resumingEngine.ExecuteAsync(runId, TestContext.Current.CancellationToken);

        var run = await resumingEngine.GetRunAsync(
            runId, TestContext.Current.CancellationToken);
        run!.TraceId.Should().HaveLength(32);
        activities.Should().Contain(item =>
            item.OperationName == ZhinuDiagnostics.Activities.WorkflowExecute &&
            item.TraceId.ToHexString() == run.TraceId);
        activities.Should().Contain(item =>
            item.OperationName == ZhinuDiagnostics.Activities.StepExecute);
        activities.SelectMany(item => item.TagObjects)
            .Should().NotContain(item =>
                item.Key.Contains("input", StringComparison.OrdinalIgnoreCase) ||
                item.Key.Contains("output", StringComparison.OrdinalIgnoreCase));
        measurements.Should().Contain(ZhinuDiagnostics.Metrics.RunsStarted)
            .And.Contain(ZhinuDiagnostics.Metrics.RunsCompleted)
            .And.Contain(ZhinuDiagnostics.Metrics.StepsExecuted)
            .And.Contain(ZhinuDiagnostics.Metrics.RunDuration);
    }

    [Fact]
    public async Task SqliteDetailedDiagnostics_EmitBoundedConnectionTelemetry()
    {
        var activities = new ConcurrentBag<Activity>();
        using var activityListener = new ActivityListener
        {
            ShouldListenTo = source =>
                source.Name == ZhinuSqliteDiagnostics.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activities.Add
        };
        ActivitySource.AddActivityListener(activityListener);

        var measurements = new ConcurrentBag<string>();
        using var meterListener = new MeterListener();
        meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == ZhinuSqliteDiagnostics.MeterName)
                listener.EnableMeasurementEvents(instrument);
        };
        meterListener.SetMeasurementEventCallback<double>(
            (instrument, _, _, _) => measurements.Add(instrument.Name));
        meterListener.Start();

        var store = new SqliteWorkflowStore(new ZhinuSqliteOptions
        {
            DatabasePath = Path.Combine(root, "diagnostics.db"),
            EnableDetailedDiagnostics = true
        });
        await store.InitializeAsync(TestContext.Current.CancellationToken);

        activities.Should().Contain(item =>
            item.OperationName == ZhinuSqliteDiagnostics.InitializeActivity);
        activities.Should().Contain(item =>
            item.OperationName == ZhinuSqliteDiagnostics.ConnectionOpenActivity);
        measurements.Should().Contain(
            ZhinuSqliteDiagnostics.ConnectionOpenDuration)
            .And.Contain(ZhinuSqliteDiagnostics.StoreOperationDuration);
        activities.Should().Contain(item =>
            item.OperationName == ZhinuSqliteDiagnostics.StoreOperationActivity);
        activities.SelectMany(item => item.TagObjects).Should().NotContain(item =>
            item.Key.EndsWith(".path", StringComparison.OrdinalIgnoreCase) ||
            item.Key.Contains("statement", StringComparison.OrdinalIgnoreCase) ||
            item.Key.Contains("query", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class ObservedWorkflow : IWorkflow<string, string>
    {
        public async Task<string> RunAsync(
            WorkflowContext context,
            string input,
            CancellationToken cancellationToken) =>
            await context.StepAsync(
                "observed-step",
                input,
                (value, _) => Task.FromResult(value),
                cancellationToken: cancellationToken);
    }
}
