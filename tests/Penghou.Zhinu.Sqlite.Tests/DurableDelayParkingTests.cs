using FluentAssertions;
using Penghou.Zhinu;
using Penghou.Zhinu.Testing;

namespace Penghou.Zhinu.Sqlite.Tests;

/// <summary>
/// Durable delays park instead of holding a worker: the run releases its lease
/// while parked and resumes when the persisted due time is reached, surviving a
/// crash. The due time is driven by the controllable clock.
/// </summary>
public sealed class DurableDelayParkingTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan Delay = TimeSpan.FromMinutes(5);

    [Fact]
    public async Task DurableDelay_ParksReleasesLeaseAndResumesOnDue()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new TestTimeProvider(Start);
        await using var host = new ZhinuTestHost(
            new WorkflowRegistry().Register("delay-park", "1", new LongDelayWorkflow()),
            timeProvider: clock);

        var runId = await host.Engine.StartAsync(
            "delay-park", "1", "x", cancellationToken: ct);
        await host.Engine.ExecuteAsync(runId, ct);

        var blocked = await host.RunUntilBlockedAsync(runId, ct);
        blocked.BlockingWaits.Should().ContainSingle()
            .Which.Kind.Should().Be(WaitKind.Delay);
        // Parking releases worker capacity: the run holds no lease while parked.
        (await host.Store.GetRunAsync(runId, ct))!.LeaseOwner.Should().BeNull();

        var advanced = await host.AdvanceToNextDurableTimerAsync(runId, ct);
        advanced.Should().Be(Delay);

        await host.Engine.ExecuteAsync(runId, ct);
        var result = await host.Engine.WaitForCompletionAsync<string>(
            runId, cancellationToken: ct);

        result.Should().Be("done:x");
        (await host.Store.GetWaitAsync(runId, "pause", ct))!.Status
            .Should().Be(WaitStatus.Completed);
    }

    [Fact]
    public async Task DurableDelay_SurvivesCrashAndResumesOnDue()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new TestTimeProvider(Start);
        var registry = new WorkflowRegistry().Register("delay-crash", "1", new LongDelayWorkflow());
        await using var host = new ZhinuTestHost(registry, timeProvider: clock);

        var runId = await host.Engine.StartAsync(
            "delay-crash", "1", "x", cancellationToken: ct);
        await host.Engine.ExecuteAsync(runId, ct);

        var reopened = host.CrashAndReopen(registry);
        var advanced = await host.AdvanceToNextDurableTimerAsync(runId, ct);
        advanced.Should().Be(Delay);

        await reopened.ExecuteAsync(runId, ct);
        var result = await reopened.WaitForCompletionAsync<string>(
            runId, cancellationToken: ct);

        result.Should().Be("done:x");
    }

    private sealed class LongDelayWorkflow : IWorkflow<string, string>
    {
        public async Task<string> RunAsync(
            WorkflowContext context, string input, CancellationToken cancellationToken)
        {
            await context.DelayAsync("pause", Delay, cancellationToken);
            return $"done:{input}";
        }
    }
}
