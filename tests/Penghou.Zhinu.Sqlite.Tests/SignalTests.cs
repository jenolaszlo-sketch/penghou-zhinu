using FluentAssertions;
using System.Text.Json;

namespace Penghou.Zhinu.Sqlite.Tests;

public sealed class SignalTests : WorkflowEngineTestBase
{

    [Fact]
    public async Task SendSignalWithReceiptAsync_IdenticalRetryReturnsCommittedReceipt()
    {
        var engine = CreateEngine(new SignalWorkflow(), "signal-receipt");
        var runId = await engine.StartAsync(
            "signal-receipt",
            "1",
            "x",
            cancellationToken: TestContext.Current.CancellationToken);
        var options = new SignalSendOptions { SignalId = Guid.NewGuid() };

        var applied = await engine.SendSignalWithReceiptAsync(
            runId,
            "approve",
            options,
            "yes",
            TestContext.Current.CancellationToken);
        var reopened = CreateEngine(new SignalWorkflow(), "signal-receipt");
        var replayed = await reopened.SendSignalWithReceiptAsync(
            runId,
            "approve",
            options,
            "yes",
            TestContext.Current.CancellationToken);

        applied.WasBuffered.Should().BeTrue();
        replayed.WasBuffered.Should().BeFalse();
        replayed.Should().BeEquivalentTo(applied, configured =>
            configured.Excluding(receipt => receipt.WasBuffered));
        (await reopened.GetSignalsAsync(
            runId,
            cancellationToken: TestContext.Current.CancellationToken))
            .Should().ContainSingle()
            .Which.Id.Should().Be(options.SignalId);
        (await reopened.GetEventsAsync(
            runId,
            cancellationToken: TestContext.Current.CancellationToken))
            .Where(item => item.EventType == WorkflowEventTypes.SignalSent)
            .Should().ContainSingle()
            .Which.Sequence.Should().Be(applied.Event.Sequence);
        applied.Event.DataJson.Should().Contain(options.SignalId.ToString("D"));
    }

    [Fact]
    public async Task SendSignalWithReceiptAsync_ConflictingReuseThrowsTypedConflict()
    {
        var engine = CreateEngine(new SignalWorkflow(), "signal-conflict");
        var runId = await engine.StartAsync(
            "signal-conflict",
            "1",
            "x",
            cancellationToken: TestContext.Current.CancellationToken);
        var options = new SignalSendOptions { SignalId = Guid.NewGuid() };
        await engine.SendSignalWithReceiptAsync(
            runId,
            "approve",
            options,
            "yes",
            TestContext.Current.CancellationToken);

        var conflict = await engine.Invoking(value =>
            value.SendSignalWithReceiptAsync(
                runId,
                "approve",
                options,
                "no",
                TestContext.Current.CancellationToken))
            .Should().ThrowAsync<WorkflowOperationConflictException>();

        conflict.Which.OperationId.Should().Be(options.SignalId);
    }

    [Fact]
    public async Task SendSignalWithReceiptAsync_CanonicalPayloadIgnoresPropertyOrder()
    {
        var engine = CreateEngine(new SignalWorkflow(), "signal-canonical");
        var runId = await engine.StartAsync(
            "signal-canonical",
            "1",
            "x",
            cancellationToken: TestContext.Current.CancellationToken);
        var options = new SignalSendOptions { SignalId = Guid.NewGuid() };
        using var firstJson = JsonDocument.Parse("{\"b\":2,\"a\":1}");
        using var reorderedJson = JsonDocument.Parse("{\"a\":1,\"b\":2}");
        await engine.SendSignalWithReceiptAsync(
            runId,
            "approve",
            options,
            firstJson.RootElement,
            TestContext.Current.CancellationToken);

        var replayed = await engine.SendSignalWithReceiptAsync(
            runId,
            "approve",
            options,
            reorderedJson.RootElement,
            TestContext.Current.CancellationToken);

        replayed.WasBuffered.Should().BeFalse();
    }

    [Fact]
    public async Task SendSignalWithReceiptAsync_ConcurrentRetryBuffersOnce()
    {
        var first = CreateEngine(new SignalWorkflow(), "signal-concurrent");
        var runId = await first.StartAsync(
            "signal-concurrent",
            "1",
            "x",
            cancellationToken: TestContext.Current.CancellationToken);
        var second = CreateEngine(new SignalWorkflow(), "signal-concurrent");
        var options = new SignalSendOptions { SignalId = Guid.NewGuid() };

        var receipts = await Task.WhenAll(
            first.SendSignalWithReceiptAsync(
                runId, "approve", options, "yes", TestContext.Current.CancellationToken),
            second.SendSignalWithReceiptAsync(
                runId, "approve", options, "yes", TestContext.Current.CancellationToken));

        receipts.Count(receipt => receipt.WasBuffered).Should().Be(1);
        receipts.Count(receipt => !receipt.WasBuffered).Should().Be(1);
        receipts.Select(receipt => receipt.Event.Sequence).Distinct()
            .Should().ContainSingle();
        (await first.GetSignalsAsync(
            runId,
            cancellationToken: TestContext.Current.CancellationToken))
            .Should().ContainSingle();
    }

    [Fact]
    public async Task SendSignalWithReceiptAsync_RetrySurvivesInboxPurge()
    {
        var engine = CreateEngine(new SignalWorkflow(), "signal-purge-receipt");
        var runId = await engine.StartAsync(
            "signal-purge-receipt",
            "1",
            "x",
            cancellationToken: TestContext.Current.CancellationToken);
        var options = new SignalSendOptions { SignalId = Guid.NewGuid() };
        var applied = await engine.SendSignalWithReceiptAsync(
            runId,
            "approve",
            options,
            "yes",
            TestContext.Current.CancellationToken);
        await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
        (await engine.PurgeSignalsAsync(
            runId,
            cancellationToken: TestContext.Current.CancellationToken)).Should().Be(1);

        var replayed = await engine.SendSignalWithReceiptAsync(
            runId,
            "approve",
            options,
            "yes",
            TestContext.Current.CancellationToken);

        replayed.WasBuffered.Should().BeFalse();
        replayed.Event.Should().BeEquivalentTo(applied.Event);
        (await engine.GetSignalsAsync(
            runId,
            cancellationToken: TestContext.Current.CancellationToken)).Should().BeEmpty();
    }

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
