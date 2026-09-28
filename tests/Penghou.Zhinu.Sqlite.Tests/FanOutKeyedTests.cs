using FluentAssertions;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;

namespace Penghou.Zhinu.Sqlite.Tests;

/// <summary>
/// Bounded stable-key fan-out: keys are validated before any delegate runs,
/// concurrency stays within the configured bound, results follow caller
/// order, incompatible inputs fail loudly, failures stop pending chunks, and
/// restarts reuse completed keyed items.
/// </summary>
public sealed class FanOutKeyedTests : WorkflowEngineTestBase
{
    [Fact]
    public async Task DuplicateKeys_RejectedBeforeAnyDelegateRuns()
    {
        var workflow = new CountingWorkflow();
        var engine = CreateEngine(workflow, "fanout-dup");
        var act = () => engine.RunAsync<string, string>(
            "fanout-dup", "1", "a,a", cancellationToken: TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<WorkflowExecutionFailedException>();
        workflow.Calls.Should().Be(0);
    }

    [Fact]
    public async Task BlankKey_Rejected()
    {
        var workflow = new CountingWorkflow();
        var engine = CreateEngine(workflow, "fanout-blank");
        var act = () => engine.RunAsync<string, string>(
            "fanout-blank", "1", " ,ok", cancellationToken: TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<WorkflowExecutionFailedException>();
        workflow.Calls.Should().Be(0);
    }

    [Fact]
    public async Task BoundedExecution_RespectsLimitAndCallerOrder()
    {
        var workflow = new ConcurrencyTrackingWorkflow();
        var engine = CreateEngine(workflow, "fanout-bound");
        var result = await engine.RunAsync<string, string>(
            "fanout-bound", "1", "a,b,c,d", cancellationToken: TestContext.Current.CancellationToken);

        result.Should().Be("A:B:C:D");
        ConcurrencyTrackingWorkflow.MaxObserved.Should().BeLessThanOrEqualTo(2);
        ConcurrencyTrackingWorkflow.Completed.Should().Be(4);
    }

    [Fact]
    public async Task IncompatibleInput_SameKey_FailsLoudly()
    {
        var engine = CreateEngine(new MismatchedFanOutWorkflow(), "fanout-mismatch");
        var act = () => engine.RunAsync<string, string>(
            "fanout-mismatch", "1", "x", cancellationToken: TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<WorkflowExecutionFailedException>();
    }

    [Fact]
    public async Task ChunkFailure_StopsPendingItems()
    {
        var workflow = new FailFastWorkflow();
        var engine = CreateEngine(workflow, "fanout-failfast");
        var act = () => engine.RunAsync<string, string>(
            "fanout-failfast", "1", "boom,never,queued", cancellationToken: TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<WorkflowExecutionFailedException>();
        workflow.Executed.Should().Equal("boom");
    }

    [Fact]
    public async Task Restart_ReusesCompletedKeyedItems()
    {
        var workflow = new CountingWorkflow();
        var engine = CreateEngine(workflow, "fanout-reuse");
        var runId = await engine.StartAsync(
            "fanout-reuse", "1", "a,b", cancellationToken: TestContext.Current.CancellationToken);
        await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
        await engine.WaitForCompletionAsync<string>(runId, cancellationToken: TestContext.Current.CancellationToken);
        workflow.Calls.Should().Be(2);

        await engine.RestartStepAsync(runId, "items.a", TestContext.Current.CancellationToken);
        await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
        var result = await engine.WaitForCompletionAsync<string>(
            runId, cancellationToken: TestContext.Current.CancellationToken);

        result.Should().Be("A:B");
        workflow.Calls.Should().Be(3);
    }

    private sealed class CountingWorkflow : IWorkflow<string, string>
    {
        public int Calls;

        public async Task<string> RunAsync(
            WorkflowContext context, string input, CancellationToken cancellationToken)
        {
            var items = input.Split(',');
            var results = await context.FanOutAsync(
                "items",
                items,
                item => item,
                async (value, _, ct) =>
                {
                    Interlocked.Increment(ref Calls);
                    await Task.Delay(10, ct);
                    return value.ToUpperInvariant();
                },
                new FanOutOptions { MaxDegreeOfParallelism = 4 },
                cancellationToken: cancellationToken);
            return string.Join(":", results);
        }
    }

    private sealed class ConcurrencyTrackingWorkflow : IWorkflow<string, string>
    {
        public static int Current;
        public static int MaxObserved;
        public static int Completed;

        public async Task<string> RunAsync(
            WorkflowContext context, string input, CancellationToken cancellationToken)
        {
            Current = 0;
            MaxObserved = 0;
            Completed = 0;
            var results = await context.FanOutAsync(
                "items",
                (IReadOnlyList<string>)input.Split(','),
                item => item,
                async (value, _, ct) =>
                {
                    var running = Interlocked.Increment(ref Current);
                    try
                    {
                        int observed;
                        do
                        {
                            observed = MaxObserved;
                        }
                        while (running > observed &&
                            Interlocked.CompareExchange(ref MaxObserved, running, observed) != observed);
                        await Task.Delay(TimeSpan.FromMilliseconds(200), ct);
                        Interlocked.Increment(ref Completed);
                        return value.ToUpperInvariant();
                    }
                    finally
                    {
                        Interlocked.Decrement(ref Current);
                    }
                },
                new FanOutOptions { MaxDegreeOfParallelism = 2 },
                cancellationToken: cancellationToken);
            return string.Join(":", results);
        }
    }

    private sealed class MismatchedFanOutWorkflow : IWorkflow<string, string>
    {
        public async Task<string> RunAsync(
            WorkflowContext context, string input, CancellationToken cancellationToken)
        {
            var first = await context.FanOutAsync(
                "items",
                (IReadOnlyList<string>)["x"],
                _ => "k",
                (value, _, _) => Task.FromResult(value),
                new FanOutOptions { MaxDegreeOfParallelism = 1 },
                cancellationToken: cancellationToken);
            var second = await context.FanOutAsync(
                "items",
                (IReadOnlyList<string>)["y"],
                _ => "k",
                (value, _, _) => Task.FromResult(value),
                new FanOutOptions { MaxDegreeOfParallelism = 1 },
                cancellationToken: cancellationToken);
            return first[0] + second[0];
        }
    }

    private sealed class FailFastWorkflow : IWorkflow<string, string>
    {
        public readonly List<string> Executed = new();
        private readonly object gate = new();

        public async Task<string> RunAsync(
            WorkflowContext context, string input, CancellationToken cancellationToken)
        {
            var results = await context.FanOutAsync(
                "items",
                (IReadOnlyList<string>)input.Split(','),
                item => item,
                (value, _, ct) =>
                {
                    lock (gate)
                        Executed.Add(value);
                    return value == "boom"
                        ? Task.FromException<string>(new InvalidOperationException("boom"))
                        : Task.FromResult(value);
                },
                new FanOutOptions { MaxDegreeOfParallelism = 1 },
                cancellationToken: cancellationToken);
            return string.Join(":", results);
        }
    }
}
