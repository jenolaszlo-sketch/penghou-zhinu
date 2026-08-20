using FluentAssertions;

namespace Penghou.Zhinu.Sqlite.Tests;

public sealed class RunDiagnosisTests : WorkflowEngineTestBase
{
    [Fact]
    public async Task DiagnoseAsync_PendingRegisteredRun_IsReady()
    {
        var engine = CreateEngine(new TwoStepWorkflow(), "diagnose-ready");
        var runId = await engine.StartAsync(
            "diagnose-ready",
            "1",
            "input",
            cancellationToken: TestContext.Current.CancellationToken);

        var diagnosis = await engine.DiagnoseAsync(
            runId,
            TestContext.Current.CancellationToken);

        diagnosis!.Code.Should().Be(RunDiagnosisCode.ReadyToExecute);
    }

    [Fact]
    public async Task DiagnoseAsync_UnregisteredRun_ExplainsMissingDefinition()
    {
        var store = CreateStore();
        var runId = await CreateRunAsync(
            store,
            "missing",
            DateTimeOffset.UtcNow,
            null,
            TestContext.Current.CancellationToken);
        var engine = CreateEngine(new WorkflowRegistry());

        var diagnosis = await engine.DiagnoseAsync(
            runId,
            TestContext.Current.CancellationToken);

        diagnosis!.Code.Should().Be(RunDiagnosisCode.MissingWorkflowRegistration);
        diagnosis.Summary.Should().Contain("missing").And.Contain("not registered");
    }

    [Fact]
    public async Task DiagnoseAsync_FailedRun_IdentifiesPermanentStepFailure()
    {
        var workflow = new FailingWorkflow();
        var engine = CreateEngine(workflow, "diagnose-failed");
        var runId = await engine.StartAsync(
            "diagnose-failed",
            "1",
            "input",
            cancellationToken: TestContext.Current.CancellationToken);
        await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);

        var diagnosis = await engine.DiagnoseAsync(
            runId,
            TestContext.Current.CancellationToken);

        diagnosis!.Code.Should().Be(RunDiagnosisCode.PermanentlyFailedStep);
        diagnosis.StepKey.Should().Be("fail");
    }

    [Fact]
    public async Task DiagnoseAsync_UnknownRun_ReturnsNull()
    {
        var engine = CreateEngine(new WorkflowRegistry());

        (await engine.DiagnoseAsync(
            Guid.NewGuid(),
            TestContext.Current.CancellationToken)).Should().BeNull();
    }
}
