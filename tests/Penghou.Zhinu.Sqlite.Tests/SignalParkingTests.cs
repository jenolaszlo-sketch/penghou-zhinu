using FluentAssertions;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;
using Penghou.Zhinu.Testing;

namespace Penghou.Zhinu.Sqlite.Tests;

/// <summary>
/// Parked signal waits: parking releases worker capacity, deadlines persist
/// across resumes and restarts, timely signals win the deadline race, and a
/// swallowed park fails the run instead of succeeding.
/// </summary>
public sealed class SignalParkingTests : WorkflowEngineTestBase
{
    [Fact]
    public async Task Deadline_PersistsAcrossResumeAndRestart()
    {
        var ct = TestContext.Current.CancellationToken;
        var engine = CreateEngine(new DeadlineWorkflow(), "park-deadline");
        var store = PeerStore();
        var runId = await engine.StartAsync("park-deadline", "1", "x", cancellationToken: ct);
        await engine.ExecuteAsync(runId, ct);
        var first = (await store.GetWaitAsync(runId, "wait", ct))!;
        first.Status.Should().Be(WaitStatus.Parked);
        first.DeadlineAt.Should().NotBeNull();

        await engine.ExecuteAsync(runId, ct);
        var resumed = (await store.GetWaitAsync(runId, "wait", ct))!;
        resumed.DeadlineAt.Should().Be(first.DeadlineAt);

        await engine.RestartStepAsync(runId, "wait", ct);
        await engine.ExecuteAsync(runId, ct);
        var restarted = (await store.GetWaitAsync(runId, "wait", ct))!;
        restarted.DeadlineAt.Should().Be(first.DeadlineAt);
        restarted.StepRevision.Should().BeGreaterThan(first.StepRevision);
    }

    [Fact]
    public async Task Cancellation_clears_parked_wait_and_prevents_late_reparking()
    {
        var ct = TestContext.Current.CancellationToken;
        var engine = CreateEngine(new DeadlineWorkflow(), "park-cancel");
        var store = PeerStore();
        var runId = await engine.StartAsync("park-cancel", "1", "x", cancellationToken: ct);
        await engine.ExecuteAsync(runId, ct);
        var parked = (await store.GetWaitAsync(runId, "wait", ct))!;
        parked.Status.Should().Be(WaitStatus.Parked);

        await engine.CancelAsync(runId, ct);
        (await store.GetWaitAsync(runId, "wait", ct))!.Status.Should().Be(WaitStatus.Cancelled);
        var repark = async () => await store.ParkWaitAsync(new ParkWaitRequest
        {
            WorkflowRunId = runId,
            StepKey = parked.StepKey,
            StepRevision = parked.StepRevision,
            StepId = parked.StepId,
            Kind = parked.Kind,
            SignalName = parked.SignalName,
            LeaseGeneration = parked.LeaseGeneration,
            Now = DateTimeOffset.UtcNow
        }, ct);
        await repark.Should().ThrowAsync<WorkflowStateException>().WithMessage("*parking refused*");
        (await store.GetWaitAsync(runId, "wait", ct))!.Status.Should().Be(WaitStatus.Cancelled);
    }

    [Fact]
    public async Task TimelySignal_WinsDeadlineRace()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new TestTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        await using var host = new ZhinuTestHost(
            new WorkflowRegistry().Register("park-race", "1", new ShortDeadlineWorkflow()),
            timeProvider: clock);
        var engine = host.Engine;
        var runId = await engine.StartAsync("park-race", "1", "x", cancellationToken: ct);
        await engine.ExecuteAsync(runId, ct);
        var parked = (await host.Store.GetWaitAsync(runId, "wait", ct))!;
        parked.Status.Should().Be(WaitStatus.Parked);
        parked.DeadlineAt.Should().Be(clock.GetUtcNow().AddMilliseconds(300));

        clock.Advance(TimeSpan.FromMilliseconds(100));
        await engine.SendSignalAsync(runId, "release", "late-but-timely", ct);
        clock.Advance(TimeSpan.FromMilliseconds(500));
        clock.GetUtcNow().Should().BeAfter(parked.DeadlineAt!.Value);

        await engine.ExecuteAsync(runId, ct);
        var result = await engine.WaitForCompletionAsync<string>(runId, cancellationToken: ct);

        result.Should().Be("late-but-timely");
    }

    [Fact]
    public async Task SwallowedPark_FailsRunLoudly()
    {
        var ct = TestContext.Current.CancellationToken;
        var engine = CreateEngine(new SwallowingWorkflow(), "park-swallow");
        var runId = await engine.StartAsync("park-swallow", "1", "x", cancellationToken: ct);

        await engine.ExecuteAsync(runId, ct);

        var run = (await engine.GetRunAsync(runId, ct))!;
        run.Status.Should().Be(WorkflowStatus.Failed);
        run.Error.Should().NotBeNull();
        run.Error!.Message.Should().Contain("unconsumed parked waits");
    }

    private SqliteWorkflowStore PeerStore() =>
        new(new ZhinuSqliteOptions
        {
            DatabasePath = Path.Combine(root, "zhinu.db"),
            BusyTimeout = TimeSpan.FromSeconds(2),
            Pooling = false
        });

    private sealed class DeadlineWorkflow : IWorkflow<string, string>
    {
        public Task<string> RunAsync(
            WorkflowContext context, string input, CancellationToken cancellationToken) =>
            context.WaitForSignalAsync<string>(
                "wait", "release", TimeSpan.FromMinutes(30), cancellationToken);
    }

    private sealed class ShortDeadlineWorkflow : IWorkflow<string, string>
    {
        public Task<string> RunAsync(
            WorkflowContext context, string input, CancellationToken cancellationToken) =>
            context.WaitForSignalAsync<string>(
                "wait", "release", TimeSpan.FromMilliseconds(300), cancellationToken);
    }

    private sealed class SwallowingWorkflow : IWorkflow<string, string>
    {
        public async Task<string> RunAsync(
            WorkflowContext context, string input, CancellationToken cancellationToken)
        {
            try
            {
                return await context.WaitForSignalAsync<string>(
                    "wait", "release", cancellationToken: cancellationToken);
            }
            catch (Exception)
            {
                return "fabricated";
            }
        }
    }
}
