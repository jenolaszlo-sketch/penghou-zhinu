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

    [Fact]
    public async Task Subscribe_IdlePolls_ThenReceivesLaterEvents()
    {
        var workflow = new TwoStepWorkflow();
        var engine = CreateEngine(workflow, "bounded-idle");
        var runId = await engine.StartAsync("bounded-idle", "1", "x", cancellationToken: TestContext.Current.CancellationToken);
        var received = new List<WorkflowEvent>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var subscriber = engine.SubscribeAsync(runId, cancellationToken: cts.Token).GetAsyncEnumerator(cts.Token);
        var collecting = Task.Run(async () =>
        {
            while (await subscriber.MoveNextAsync())
            {
                received.Add(subscriber.Current);
                if (subscriber.Current.EventType == WorkflowEventTypes.WorkflowCompleted)
                    return;
            }
        }, TestContext.Current.CancellationToken);
        try
        {
            // Let the subscriber sit through quiet polls with no new events.
            await Task.Delay(300, TestContext.Current.CancellationToken);
            await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
            await collecting.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
        finally
        {
            await cts.CancelAsync();
            await subscriber.DisposeAsync();
        }
        await collecting;
        var final = await engine.GetRunAsync(runId, TestContext.Current.CancellationToken);
        received.Should().Contain(
            e => e.EventType == WorkflowEventTypes.WorkflowCompleted,
            $"run ended {final?.Status} with error {final?.Error?.Message}");
        engine.SubscriptionChannelCount.Should().Be(0);
    }

    [Fact]
    public async Task Subscribe_MultipleSubscribers_ReceiveCompletion()
    {
        var workflow = new TwoStepWorkflow();
        var engine = CreateEngine(workflow, "bounded-multi");
        var runId = await engine.StartAsync("bounded-multi", "1", "x", cancellationToken: TestContext.Current.CancellationToken);
        await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
        await engine.WaitForCompletionAsync<string>(runId, cancellationToken: TestContext.Current.CancellationToken);

        async Task<List<WorkflowEvent>> CollectAsync()
        {
            var events = new List<WorkflowEvent>();
            var subscriber = engine.SubscribeAsync(runId).GetAsyncEnumerator(TestContext.Current.CancellationToken);
            while (await subscriber.MoveNextAsync())
                events.Add(subscriber.Current);
            await subscriber.DisposeAsync();
            return events;
        }
        var results = await Task.WhenAll(CollectAsync(), CollectAsync());

        results.Should().HaveCount(2);
        foreach (var events in results)
            events.Should().Contain(e => e.EventType == WorkflowEventTypes.WorkflowCompleted);
        engine.SubscriptionChannelCount.Should().Be(0);
    }
}
