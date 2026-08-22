using FluentAssertions;
#pragma warning disable xUnit1051

namespace Penghou.Zhinu.Sqlite.Tests;

public sealed class DeterministicDiagnosticsTests : WorkflowEngineTestBase
{
    private static (RunDiagnosisCode Code, string StepKey, string[] Blocking) Snapshot(RunDiagnosis d) =>
        (d.Code, d.StepKey ?? string.Empty, d.BlockingStepKeys.OrderBy(k => k).ToArray());

    [Fact]
    public async Task Diagnose_WaitingForSignal_IsDeterministic()
    {
        var workflow = new SignalWorkflow();
        var engine = CreateEngine(workflow, "diag-signal");
        var runId = await engine.StartAsync("diag-signal", "1", "x", cancellationToken: TestContext.Current.CancellationToken);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var exec = engine.ExecuteAsync(runId, cts.Token);
        await WaitUntilAsync(() => HasStepStatusAsync(engine, runId, "approval", StepStatus.Waiting, cts.Token), cts.Token);

        var first = await engine.DiagnoseAsync(runId, TestContext.Current.CancellationToken);
        var second = await engine.DiagnoseAsync(runId, TestContext.Current.CancellationToken);
        Snapshot(first!).Should().Be(Snapshot(second!));
        first!.Code.Should().Be(RunDiagnosisCode.WaitingForSignal);

        await cts.CancelAsync();
        try { await exec; } catch { }
    }

    [Fact]
    public async Task Diagnose_Terminal_IsDeterministic()
    {
        var workflow = new TwoStepWorkflow();
        var engine = CreateEngine(workflow, "diag-terminal");
        var runId = await engine.StartAsync("diag-terminal", "1", "x", cancellationToken: TestContext.Current.CancellationToken);
        await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
        await engine.WaitForCompletionAsync<string>(runId, cancellationToken: TestContext.Current.CancellationToken);

        var first = await engine.DiagnoseAsync(runId, TestContext.Current.CancellationToken);
        var second = await engine.DiagnoseAsync(runId, TestContext.Current.CancellationToken);
        Snapshot(first!).Should().Be(Snapshot(second!));
        first!.Code.Should().Be(RunDiagnosisCode.Terminal);
    }
}
