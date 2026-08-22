using FluentAssertions;
#pragma warning disable xUnit1051

namespace Penghou.Zhinu.Sqlite.Tests;

public sealed class BoundedResourceTests : WorkflowEngineTestBase
{
    [Fact]
    public async Task Subscribe_DisconnectedSubscriber_ReleasesWakeupChannel()
    {
        var workflow = new TwoStepWorkflow();
        var engine = CreateEngine(workflow, "bounded-subscribe");
        var runId = await engine.StartAsync("bounded-subscribe", "1", "x", cancellationToken: TestContext.Current.CancellationToken);

        // Subscribe and then disconnect before the run terminates.
        using var cts = new CancellationTokenSource();
        var subscriber = engine.SubscribeAsync(runId, cancellationToken: cts.Token).GetAsyncEnumerator(cts.Token);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        await cts.CancelAsync();
        await subscriber.DisposeAsync();

        // The abandoned wakeup channel must have been released.
        engine.SubscriptionChannelCount.Should().Be(0);
    }

    [Fact]
    public async Task Subscribe_TerminalRun_ReleasesWakeupChannel()
    {
        var workflow = new TwoStepWorkflow();
        var engine = CreateEngine(workflow, "bounded-terminal");
        var runId = await engine.StartAsync("bounded-terminal", "1", "x", cancellationToken: TestContext.Current.CancellationToken);
        await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
        await engine.WaitForCompletionAsync<string>(runId, cancellationToken: TestContext.Current.CancellationToken);

        var subscriber = engine.SubscribeAsync(runId).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        while (await subscriber.MoveNextAsync()) { }
        await subscriber.DisposeAsync();

        engine.SubscriptionChannelCount.Should().Be(0);
    }

    [Fact]
    public async Task Subscribe_ReconnectsAfterDisconnect()
    {
        var workflow = new TwoStepWorkflow();
        var engine = CreateEngine(workflow, "bounded-reconnect");
        var runId = await engine.StartAsync("bounded-reconnect", "1", "x", cancellationToken: TestContext.Current.CancellationToken);

        // First subscriber disconnects.
        using (var cts = new CancellationTokenSource())
        {
            var subscriber = engine.SubscribeAsync(runId, cancellationToken: cts.Token).GetAsyncEnumerator(cts.Token);
            await Task.Delay(30, TestContext.Current.CancellationToken);
            await cts.CancelAsync();
            await subscriber.DisposeAsync();
        }
        engine.SubscriptionChannelCount.Should().Be(0);

        // A later subscriber recreates the channel and still streams completion events.
        await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
        var events = new List<WorkflowEvent>();
        var reconnect = engine.SubscribeAsync(runId).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        while (await reconnect.MoveNextAsync())
            events.Add(reconnect.Current);
        await reconnect.DisposeAsync();
        events.Should().Contain(e => e.EventType == WorkflowEventTypes.WorkflowCompleted);
    }
}
