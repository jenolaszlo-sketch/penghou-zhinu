using FluentAssertions;
#pragma warning disable xUnit1051

namespace Penghou.Zhinu.Sqlite.Tests;

public sealed class SignalRetentionTests : WorkflowEngineTestBase
{
    [Fact]
    public async Task ListSignals_BufferedAndConsumed_Paginated()
    {
        var workflow = new SignalWorkflow();
        var engine = CreateEngine(workflow, "signal-list");
        var runId = await engine.StartAsync("signal-list", "1", "x", cancellationToken: TestContext.Current.CancellationToken);
        // Buffer two signals before the wait exists.
        await engine.SendSignalAsync(runId, "approve", "one", TestContext.Current.CancellationToken);
        await engine.SendSignalAsync(runId, "approve", "two", TestContext.Current.CancellationToken);
        await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
        await engine.WaitForCompletionAsync<string>(runId, cancellationToken: TestContext.Current.CancellationToken);

        var all = await engine.GetSignalsAsync(runId, cancellationToken: TestContext.Current.CancellationToken);
        all.Should().HaveCount(2);
        // Oldest delivered first; exactly one consumed, one buffered.
        all.Count(s => s.Status == SignalStatus.Buffered).Should().Be(1);
        all.Count(s => s.Status == SignalStatus.Consumed).Should().Be(1);
        all[0].Status.Should().Be(SignalStatus.Consumed);
        all[0].DeliveredStepId.Should().NotBeNull();

        // Cursor pagination yields the same set without duplicates.
        var page1 = await engine.GetSignalsAsync(runId, new SignalQuery { Limit = 1 }, TestContext.Current.CancellationToken);
        page1.Should().HaveCount(1);
        var page2 = await engine.GetSignalsAsync(runId, new SignalQuery { Limit = 1, AfterId = page1[0].Id }, TestContext.Current.CancellationToken);
        page2.Should().HaveCount(1);
        page1.Concat(page2).Select(s => s.Id).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task ListSignals_FilterByStatusAndName()
    {
        var workflow = new SignalWorkflow();
        var engine = CreateEngine(workflow, "signal-filter");
        var runId = await engine.StartAsync("signal-filter", "1", "x", cancellationToken: TestContext.Current.CancellationToken);
        await engine.SendSignalAsync(runId, "approve", "yes", TestContext.Current.CancellationToken);
        await engine.SendSignalAsync(runId, "other", "nope", TestContext.Current.CancellationToken);
        await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
        await engine.WaitForCompletionAsync<string>(runId, cancellationToken: TestContext.Current.CancellationToken);

        var consumed = await engine.GetSignalsAsync(runId, new SignalQuery { Status = SignalStatus.Consumed }, TestContext.Current.CancellationToken);
        consumed.Should().ContainSingle().Which.SignalName.Should().Be("approve");
        var buffered = await engine.GetSignalsAsync(runId, new SignalQuery { Status = SignalStatus.Buffered }, TestContext.Current.CancellationToken);
        buffered.Should().ContainSingle().Which.SignalName.Should().Be("other");
        var byName = await engine.GetSignalsAsync(runId, new SignalQuery { SignalName = "other" }, TestContext.Current.CancellationToken);
        byName.Should().ContainSingle().Which.Status.Should().Be(SignalStatus.Buffered);
    }

    [Fact]
    public async Task PurgeSignals_BoundsInboxWithoutLosingHistory()
    {
        var workflow = new SignalWorkflow();
        var engine = CreateEngine(workflow, "signal-purge");
        var runId = await engine.StartAsync("signal-purge", "1", "x", cancellationToken: TestContext.Current.CancellationToken);
        await engine.SendSignalAsync(runId, "approve", "yes", TestContext.Current.CancellationToken);
        await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
        await engine.WaitForCompletionAsync<string>(runId, cancellationToken: TestContext.Current.CancellationToken);

        // Default purge removes consumed rows only.
        var purged = await engine.PurgeSignalsAsync(runId, cancellationToken: TestContext.Current.CancellationToken);
        purged.Should().Be(1);
        var remaining = await engine.GetSignalsAsync(runId, cancellationToken: TestContext.Current.CancellationToken);
        remaining.Should().BeEmpty();

        // The durable audit history is untouched.
        var events = await engine.GetEventsAsync(runId, cancellationToken: TestContext.Current.CancellationToken);
        events.Should().Contain(e => e.EventType == WorkflowEventTypes.SignalDelivered);
        events.Should().Contain(e => e.EventType == WorkflowEventTypes.SignalSent);
    }

    [Fact]
    public async Task PurgeSignals_BufferedOlderThan_RemovesOnlyOldBuffered()
    {
        var workflow = new SignalWorkflow();
        var engine = CreateEngine(workflow, "signal-purge-old");
        var runId = await engine.StartAsync("signal-purge-old", "1", "x", cancellationToken: TestContext.Current.CancellationToken);
        // Two buffered signals never consumed (run waits, but we won't deliver); plus one delivered.
        await engine.SendSignalAsync(runId, "stale1", "a", TestContext.Current.CancellationToken);
        await engine.SendSignalAsync(runId, "stale2", "b", TestContext.Current.CancellationToken);
        await engine.SendSignalAsync(runId, "approve", "yes", TestContext.Current.CancellationToken);
        await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
        await engine.WaitForCompletionAsync<string>(runId, cancellationToken: TestContext.Current.CancellationToken);

        var cut = DateTimeOffset.UtcNow.AddMinutes(1);
        var purged = await engine.PurgeSignalsAsync(
            runId,
            new SignalPurgeOptions { Status = SignalStatus.Buffered, OlderThan = cut },
            TestContext.Current.CancellationToken);
        purged.Should().Be(2);
        var remaining = await engine.GetSignalsAsync(runId, cancellationToken: TestContext.Current.CancellationToken);
        remaining.Should().ContainSingle().Which.Status.Should().Be(SignalStatus.Consumed);
    }

    [Fact]
    public async Task ListSignals_UnknownRun_ReturnsEmpty()
    {
        var workflow = new SignalWorkflow();
        var engine = CreateEngine(workflow, "signal-unknown");
        var signals = await engine.GetSignalsAsync(Guid.NewGuid(), cancellationToken: TestContext.Current.CancellationToken);
        signals.Should().BeEmpty();
    }
}
