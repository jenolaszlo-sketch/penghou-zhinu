using FluentAssertions;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;

namespace Penghou.Zhinu.Sqlite.Tests;

/// <summary>
/// Parked retry backoff resumes across executions, and blocked runs report
/// their precise parked waits.
/// </summary>
public sealed class RetryParkingTests : WorkflowEngineTestBase
{
    [Fact]
    public async Task BackoffParks_ThenResumesAfterDue()
    {
        var ct = TestContext.Current.CancellationToken;
        var workflow = new FlakyBackoffWorkflow();
        var engine = CreateEngine(workflow, "retry-park");
        var store = PeerStore();
        var runId = await engine.StartAsync("retry-park", "1", "x", cancellationToken: ct);
        await engine.ExecuteAsync(runId, ct);

        var wait = (await store.GetWaitAsync(runId, "flaky", ct))!;
        wait.Kind.Should().Be(WaitKind.Retry);
        wait.Status.Should().Be(WaitStatus.Parked);
        wait.AvailableAt.Should().NotBeNull();

        await Task.Delay(TimeSpan.FromMilliseconds(600), ct);
        await engine.ExecuteAsync(runId, ct);
        var result = await engine.WaitForCompletionAsync<string>(runId, cancellationToken: ct);

        result.Should().Be("recovered:x");
        workflow.Calls.Should().Be(2);
        (await store.GetWaitAsync(runId, "flaky", ct))!.Status
            .Should().Be(WaitStatus.Completed);
    }

    [Fact]
    public async Task WaitUntilBlocked_ReportsParkedSignalWait()
    {
        var ct = TestContext.Current.CancellationToken;
        var engine = CreateEngine(new SignalWorkflow(), "blocked-reason");
        var runId = await engine.StartAsync("blocked-reason", "1", "x", cancellationToken: ct);
        await engine.ExecuteAsync(runId, ct);

        var blocked = await engine.WaitUntilBlockedAsync(runId, ct);

        blocked.Run.Id.Should().Be(runId);
        blocked.BlockingWaits.Should().ContainSingle()
            .Which.Should().Match<WorkflowWait>(w =>
                w.Kind == WaitKind.Signal && w.Status == WaitStatus.Parked);
    }

    [Fact]
    public async Task WaitUntilBlocked_TerminalRun_HasNoWaits()
    {
        var ct = TestContext.Current.CancellationToken;
        var engine = CreateEngine(new TwoStepWorkflow(), "blocked-terminal");
        var runId = await engine.StartAsync(
            "blocked-terminal", "1", "x", cancellationToken: ct);
        await engine.ExecuteAsync(runId, ct);
        await engine.WaitForCompletionAsync<string>(runId, cancellationToken: ct);

        var blocked = await engine.WaitUntilBlockedAsync(runId, ct);

        blocked.Run.Status.Should().Be(WorkflowStatus.Completed);
        blocked.BlockingWaits.Should().BeEmpty();
    }

    private SqliteWorkflowStore PeerStore() =>
        new(new ZhinuSqliteOptions
        {
            DatabasePath = Path.Combine(root, "zhinu.db"),
            BusyTimeout = TimeSpan.FromSeconds(2),
            Pooling = false
        });

    private sealed class FlakyBackoffWorkflow : IWorkflow<string, string>
    {
        public int Calls;

        public Task<string> RunAsync(
            WorkflowContext context, string input, CancellationToken cancellationToken) =>
            context.StepAsync(
                "flaky",
                input,
                (_, _) =>
                {
                    if (Interlocked.Increment(ref Calls) == 1)
                        throw new InvalidOperationException("transient");
                    return Task.FromResult($"recovered:{input}");
                },
                new StepOptions { Retry = new RetryPolicy { MaxAttempts = 2, InitialDelay = TimeSpan.FromMilliseconds(300) } },
                cancellationToken);
    }
}
