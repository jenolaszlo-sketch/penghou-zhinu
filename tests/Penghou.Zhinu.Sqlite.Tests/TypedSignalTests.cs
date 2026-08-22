using FluentAssertions;
#pragma warning disable xUnit1051

namespace Penghou.Zhinu.Sqlite.Tests;

public sealed class TypedSignalTests : WorkflowEngineTestBase
{
    private static readonly SignalDefinition<string> Approve = new("approve");
    private static readonly SignalDefinition<int> Count = new("count");

    [Fact]
    public async Task TypedSignal_RoundTrip_StringPayload()
    {
        var workflow = new TypedStringSignalWorkflow();
        var engine = CreateEngine(workflow, "typed-signal-string");
        var runId = await engine.StartAsync("typed-signal-string", "1", "x", cancellationToken: TestContext.Current.CancellationToken);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var exec = engine.ExecuteAsync(runId, cts.Token);
        await WaitUntilAsync(() => HasStepStatusAsync(engine, runId, "approval", StepStatus.Waiting, cts.Token), cts.Token);
        await engine.SendSignalAsync(runId, Approve, "yes", cts.Token);
        await exec;
        var result = await engine.WaitForCompletionAsync<string>(runId, cancellationToken: TestContext.Current.CancellationToken);
        result.Should().Be("yes");
    }

    [Fact]
    public async Task TypedSignal_RoundTrip_IntPayload()
    {
        var workflow = new TypedIntSignalWorkflow();
        var engine = CreateEngine(new WorkflowRegistry().Register("typed-signal-int", "1", workflow));
        var runId = await engine.StartAsync("typed-signal-int", "1", "x", cancellationToken: TestContext.Current.CancellationToken);
        await engine.SendSignalAsync(runId, Count, 42, TestContext.Current.CancellationToken);
        await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
        var result = await engine.WaitForCompletionAsync<int>(runId, cancellationToken: TestContext.Current.CancellationToken);
        result.Should().Be(42);
    }

    [Fact]
    public async Task TypedSignal_MalformedPayload_ThrowsSerialization()
    {
        var workflow = new TypedIntSignalWorkflow();
        var engine = CreateEngine(new WorkflowRegistry().Register("typed-signal-malformed", "1", workflow));
        var runId = await engine.StartAsync("typed-signal-malformed", "1", "x", cancellationToken: TestContext.Current.CancellationToken);
        await engine.SendSignalAsync(runId, "count", "not-an-int", TestContext.Current.CancellationToken);
        await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
        var act = () => engine.WaitForCompletionAsync<int>(runId, cancellationToken: TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<WorkflowExecutionFailedException>();
        var run = await engine.GetRunAsync(runId, TestContext.Current.CancellationToken);
        run!.Status.Should().Be(WorkflowStatus.Failed);
    }

    [Fact]
    public async Task TypedSignal_ValueTypeNull_ThrowsSerialization()
    {
        var workflow = new TypedIntSignalWorkflow();
        var engine = CreateEngine(new WorkflowRegistry().Register("typed-signal-null-int", "1", workflow));
        var runId = await engine.StartAsync("typed-signal-null-int", "1", "x", cancellationToken: TestContext.Current.CancellationToken);
        await engine.SendSignalAsync(runId, "count", null, TestContext.Current.CancellationToken);
        await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
        var act = () => engine.WaitForCompletionAsync<int>(runId, cancellationToken: TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<WorkflowExecutionFailedException>();
    }

    [Fact]
    public async Task TypedSignal_ConcurrentSenders_ExactlyOnce()
    {
        var workflow = new TypedStringSignalWorkflow();
        var engine = CreateEngine(workflow, "typed-signal-concurrent");
        var runId = await engine.StartAsync("typed-signal-concurrent", "1", "x", cancellationToken: TestContext.Current.CancellationToken);
        // Buffer two signals concurrently before wait
        await Task.WhenAll(
            engine.SendSignalAsync(runId, Approve, "first", TestContext.Current.CancellationToken),
            engine.SendSignalAsync(runId, Approve, "second", TestContext.Current.CancellationToken));
        await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
        var result = await engine.WaitForCompletionAsync<string>(runId, cancellationToken: TestContext.Current.CancellationToken);
        // Oldest buffered wins (first or second, but exactly one)
        result.Should().BeOneOf("first", "second");
        var events = await engine.GetEventsAsync(runId, cancellationToken: TestContext.Current.CancellationToken);
        events.Count(e => e.EventType == WorkflowEventTypes.SignalDelivered).Should().Be(1);
        events.Count(e => e.EventType == WorkflowEventTypes.SignalSent).Should().Be(2);
    }

    [Fact]
    public async Task TypedSignal_DuplicateConsumption_OnlyOneWaiter()
    {
        var engine = CreateEngine(new TypedStringSignalWorkflow(), "typed-signal-dup");
        var runId = await engine.StartAsync("typed-signal-dup", "1", "x", cancellationToken: TestContext.Current.CancellationToken);
        await engine.SendSignalAsync(runId, Approve, "only-once", TestContext.Current.CancellationToken);
        await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
        var result = await engine.WaitForCompletionAsync<string>(runId, cancellationToken: TestContext.Current.CancellationToken);
        result.Should().Be("only-once");
        // Second consumption attempt via new run waiting for same signal should not steal already-delivered signal
        var run2 = await engine.StartAsync("typed-signal-dup", "1", "y", cancellationToken: TestContext.Current.CancellationToken);
        await engine.SendSignalAsync(run2, Approve, "second-run", TestContext.Current.CancellationToken);
        await engine.ExecuteAsync(run2, TestContext.Current.CancellationToken);
        var result2 = await engine.WaitForCompletionAsync<string>(run2, cancellationToken: TestContext.Current.CancellationToken);
        result2.Should().Be("second-run");
    }

    private sealed class TypedStringSignalWorkflow : IWorkflow<string, string>
    {
        public async Task<string> RunAsync(WorkflowContext ctx, string input, CancellationToken ct) =>
            await ctx.WaitForSignalAsync("approval", Approve, cancellationToken: ct);
    }

    private sealed class TypedIntSignalWorkflow : IWorkflow<string, int>
    {
        public async Task<int> RunAsync(WorkflowContext ctx, string input, CancellationToken ct) =>
            await ctx.WaitForSignalAsync("approval", Count, cancellationToken: ct);
    }
}
