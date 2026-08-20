using FluentAssertions;

namespace Penghou.Zhinu.Sqlite.Tests;

public sealed class CompensationTests : WorkflowEngineTestBase
{

    [Fact]
    public async Task StepAsync_WithCompensation_PersistsPendingCompensationWithCommittedResult()
    {
        var workflow = new CompensationWorkflow();
        var engine = CreateEngine(workflow, "compensate");
        var result = await engine.RunAsync<string, string>(
            "compensate",
            "1",
            "x",
            cancellationToken: TestContext.Current.CancellationToken);

        result.Should().Be("confirmed:vm-x");
        workflow.ReserveCalls.Should().Be(1);
        workflow.CompensationCalls.Should().Be(0);

        var compensations = await engine.GetCompensationsAsync(
            workflow.RunId,
            TestContext.Current.CancellationToken);
        compensations.Should().ContainSingle();
        var compensation = compensations.Single();
        compensation.StepKey.Should().Be("reserve");
        compensation.Revision.Should().Be(1);
        compensation.Status.Should().Be(CompensationStatus.Pending);
        compensation.CompensationName.Should().Be("reserve");
        compensation.InputJson.Should().Be("\"vm-x\"");
        compensation.InputType.Should().NotBeNullOrEmpty();
        compensation.IdempotencyKey.Should().Be(
            $"{workflow.RunId:D}:reserve:1:compensation");
        compensation.RetryPolicyJson.Should().NotBeNullOrEmpty();
        compensation.LeaseGeneration.Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task StepAsync_WithoutCompensation_RecordsNoCompensations()
    {
        var workflow = new TwoStepWorkflow();
        var engine = CreateEngine(workflow, "no-compensate");
        await engine.RunAsync<string, string>(
            "no-compensate",
            "1",
            "value",
            cancellationToken: TestContext.Current.CancellationToken);

        (await engine.GetCompensationsAsync(
            workflow.RunId,
            TestContext.Current.CancellationToken)).Should().BeEmpty();
    }

    [Fact]
    public async Task StepAsync_CompensatedStepTerminalFailure_MarksCompensationSkipped()
    {
        var workflow = new FailingCompensatedWorkflow();
        var engine = CreateEngine(workflow, "compensate-fail");
        var runId = await engine.StartAsync(
            "compensate-fail",
            "1",
            "x",
            cancellationToken: TestContext.Current.CancellationToken);
        await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);

        (await engine.GetRunAsync(
            runId,
            TestContext.Current.CancellationToken))!.Status
            .Should().Be(WorkflowStatus.Failed);
        var compensations = await engine.GetCompensationsAsync(
            runId,
            TestContext.Current.CancellationToken);
        compensations.Should().ContainSingle()
            .Which.Status.Should().Be(CompensationStatus.Skipped);
    }

    [Fact]
    public async Task StepAsync_CompensatedStepRestart_CreatesCompensationForNewRevision()
    {
        var workflow = new CompensationWorkflow();
        var engine = CreateEngine(workflow, "compensate-restart");
        await engine.RunAsync<string, string>(
            "compensate-restart",
            "1",
            "x",
            cancellationToken: TestContext.Current.CancellationToken);
        var runId = workflow.RunId;

        await engine.RestartStepAsync(
            runId,
            "reserve",
            cancellationToken: TestContext.Current.CancellationToken);

        var resumed = new CompensationWorkflow();
        var resumedEngine = CreateEngine(resumed, "compensate-restart");
        await resumedEngine.ExecuteAsync(
            runId,
            TestContext.Current.CancellationToken);
        var rerun = await resumedEngine.WaitForCompletionAsync<string>(
            runId,
            cancellationToken: TestContext.Current.CancellationToken);

        rerun.Should().Be("confirmed:vm-x");
        (await resumedEngine.GetStepsAsync(
            runId,
            TestContext.Current.CancellationToken)).Should().Contain(item =>
            item.StepKey == "reserve" && item.Revision == 2);

        var compensations = await resumedEngine.GetCompensationsAsync(
            runId,
            TestContext.Current.CancellationToken);
        compensations.Should().HaveCount(2);
        compensations.Select(item => item.Revision).Should().Equal(1, 2);
        compensations.Should().OnlyContain(item =>
            item.Status == CompensationStatus.Pending &&
            item.InputJson == "\"vm-x\"");
    }
}
