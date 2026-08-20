using FluentAssertions;

namespace Penghou.Zhinu.Sqlite.Tests;

public sealed class SignalTests : WorkflowEngineTestBase
{

    [Fact]
    public async Task WaitForSignalAsync_SignalDeliveredAfterWait_CompletesWorkflow()
    {
        var workflow = new SignalWorkflow();
        var engine = CreateEngine(workflow, "signal");
        var runId = await engine.StartAsync(
            "signal",
            "1",
            "x",
            cancellationToken: TestContext.Current.CancellationToken);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var execution = engine.ExecuteAsync(runId, cts.Token);
        await WaitUntilAsync(
            () => HasStepStatusAsync(
                engine,
                runId,
                "approval",
                StepStatus.Waiting,
                cts.Token),
            cts.Token);
        await engine.SendSignalAsync(runId, "approve", "yes", cts.Token);
        await execution;

        var result = await engine.WaitForCompletionAsync<string>(
            runId,
            cancellationToken: TestContext.Current.CancellationToken);
        result.Should().Be("yes");
        (await engine.GetRunAsync(
            runId,
            TestContext.Current.CancellationToken))!.Status.Should().Be(WorkflowStatus.Completed);
    }

    [Fact]
    public async Task WaitForSignalAsync_SignalBufferedBeforeWait_IsDeliveredImmediately()
    {
        var workflow = new SignalWorkflow();
        var engine = CreateEngine(workflow, "signal-buffered");
        var runId = await engine.StartAsync(
            "signal-buffered",
            "1",
            "x",
            cancellationToken: TestContext.Current.CancellationToken);

        await engine.SendSignalAsync(
            runId,
            "approve",
            "yes",
            TestContext.Current.CancellationToken);
        await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);

        var result = await engine.WaitForCompletionAsync<string>(
            runId,
            cancellationToken: TestContext.Current.CancellationToken);
        result.Should().Be("yes");
        var events = await engine.GetEventsAsync(
            runId,
            cancellationToken: TestContext.Current.CancellationToken);
        events.Should().Contain(item => item.EventType == WorkflowEventTypes.SignalSent);
        events.Should().Contain(item => item.EventType == WorkflowEventTypes.SignalDelivered);
    }

    [Fact]
    public async Task WaitForSignalAsync_Timeout_FailsRun()
    {
        var workflow = new SignalWorkflow
        {
            WaitTimeout = TimeSpan.FromMilliseconds(150)
        };
        var engine = CreateEngine(workflow, "signal-timeout");
        var runId = await engine.StartAsync(
            "signal-timeout",
            "1",
            "x",
            cancellationToken: TestContext.Current.CancellationToken);

        await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);

        var action = () => engine.WaitForCompletionAsync<string>(
            runId,
            cancellationToken: TestContext.Current.CancellationToken);
        await action.Should().ThrowAsync<WorkflowExecutionFailedException>()
            .WithMessage("*not delivered before the wait deadline*");
        (await engine.GetRunAsync(
            runId,
            TestContext.Current.CancellationToken))!.Status.Should().Be(WorkflowStatus.Failed);
        (await engine.GetStepsAsync(
            runId,
            TestContext.Current.CancellationToken)).Single(item => item.StepKey == "approval")
            .Status.Should().Be(StepStatus.Waiting);
    }

    [Fact]
    public async Task WaitForSignalAsync_SurvivesInterruption_ThenReceivesLateSignal()
    {
        var workflow = new SignalWorkflow();
        var engine = CreateEngine(workflow, "signal-interrupt");
        var runId = await engine.StartAsync(
            "signal-interrupt",
            "1",
            "x",
            cancellationToken: TestContext.Current.CancellationToken);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var interruption = new CancellationTokenSource();
        var execution = engine.ExecuteAsync(runId, interruption.Token);
        await WaitUntilAsync(
            () => HasStepStatusAsync(
                engine,
                runId,
                "approval",
                StepStatus.Waiting,
                cts.Token),
            cts.Token);
        await interruption.CancelAsync();
        await execution;
        (await engine.GetRunAsync(
            runId,
            TestContext.Current.CancellationToken))!.Status.Should().Be(WorkflowStatus.Running);
        (await engine.GetStepsAsync(
            runId,
            TestContext.Current.CancellationToken)).Single(item => item.StepKey == "approval")
            .Status.Should().Be(StepStatus.Waiting);

        await engine.SendSignalAsync(
            runId,
            "approve",
            "yes",
            TestContext.Current.CancellationToken);
        await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
        var result = await engine.WaitForCompletionAsync<string>(
            runId,
            cancellationToken: TestContext.Current.CancellationToken);
        result.Should().Be("yes");
    }
}
