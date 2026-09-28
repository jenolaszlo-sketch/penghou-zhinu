using FluentAssertions;
using Penghou.Zhinu;
using Penghou.Zhinu.Testing;

namespace Penghou.Zhinu.Sqlite.Tests;

/// <summary>
/// The controllable clock fires timers only on advancement, so retry backoff
/// and other parked durable waits progress deterministically without real
/// waiting. An infinite signal wait exposes no durable timer to advance to.
/// </summary>
public sealed class FakeClockAdvancementTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Advance_RejectsBackwardsMovement()
    {
        var clock = new TestTimeProvider(Start);

        var act = () => clock.Advance(TimeSpan.FromTicks(-1));

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task Advance_FiresDelayOnlyOnceDue()
    {
        var clock = new TestTimeProvider(Start);
        var pending = Task.Delay(
            TimeSpan.FromSeconds(5), clock, TestContext.Current.CancellationToken);

        pending.IsCompleted.Should().BeFalse();
        clock.Advance(TimeSpan.FromSeconds(4));
        pending.IsCompleted.Should().BeFalse();
        clock.Advance(TimeSpan.FromSeconds(1));
        pending.IsCompleted.Should().BeTrue();

        await pending;
    }

    [Fact]
    public void Advance_FiresCancellationTimeout()
    {
        var clock = new TestTimeProvider(Start);
        using var source = new CancellationTokenSource(TimeSpan.FromMinutes(1), clock);

        clock.Advance(TimeSpan.FromSeconds(59));
        source.IsCancellationRequested.Should().BeFalse();
        clock.Advance(TimeSpan.FromSeconds(1));
        source.IsCancellationRequested.Should().BeTrue();
    }

    [Fact]
    public async Task Advance_DoesNotFireInfiniteDelay()
    {
        var clock = new TestTimeProvider(Start);
        using var source = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        var pending = Task.Delay(Timeout.InfiniteTimeSpan, clock, source.Token);

        clock.Advance(TimeSpan.FromDays(365));
        pending.IsCompleted.Should().BeFalse();

        await source.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public void Advance_FiresPeriodicTimerEachPeriod()
    {
        var clock = new TestTimeProvider(Start);
        var count = 0;
        using var timer = clock.CreateTimer(
            _ => count++,
            state: null,
            dueTime: TimeSpan.FromSeconds(1),
            period: TimeSpan.FromSeconds(1));

        clock.Advance(TimeSpan.FromSeconds(1));
        count.Should().Be(1);
        clock.Advance(TimeSpan.FromSeconds(1));
        count.Should().Be(2);
    }

    [Fact]
    public async Task RetryBackoff_ResumesByAdvancingDurableTimer()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new TestTimeProvider(Start);
        var workflow = new FlakyBackoffWorkflow();
        await using var host = new ZhinuTestHost(
            new WorkflowRegistry().Register("fake-retry", "1", workflow),
            timeProvider: clock);

        var runId = await host.Engine.StartAsync(
            "fake-retry", "1", "x", cancellationToken: ct);
        await host.Engine.ExecuteAsync(runId, ct);

        var blocked = await host.RunUntilBlockedAsync(runId, ct);
        blocked.BlockingWaits.Should().ContainSingle()
            .Which.Kind.Should().Be(WaitKind.Retry);

        var advanced = await host.AdvanceToNextDurableTimerAsync(runId, ct);
        advanced.Should().Be(TimeSpan.FromMilliseconds(300));
        clock.GetUtcNow().Should().Be(Start.AddMilliseconds(300));

        await host.Engine.ExecuteAsync(runId, ct);
        var result = await host.Engine.WaitForCompletionAsync<string>(
            runId, cancellationToken: ct);

        result.Should().Be("recovered:x");
        workflow.Calls.Should().Be(2);
    }

    [Fact]
    public async Task InfiniteSignalWait_ExposesNoDurableTimer()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new TestTimeProvider(Start);
        await using var host = new ZhinuTestHost(
            new WorkflowRegistry().Register("fake-signal", "1", new InfiniteSignalWorkflow()),
            timeProvider: clock);

        var runId = await host.Engine.StartAsync(
            "fake-signal", "1", "x", cancellationToken: ct);
        await host.Engine.ExecuteAsync(runId, ct);

        var blocked = await host.RunUntilBlockedAsync(runId, ct);
        blocked.BlockingWaits.Should().ContainSingle()
            .Which.Kind.Should().Be(WaitKind.Signal);

        var advanced = await host.AdvanceToNextDurableTimerAsync(runId, ct);

        advanced.Should().BeNull();
    }

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
                new StepOptions
                {
                    Retry = new RetryPolicy
                    {
                        MaxAttempts = 2,
                        InitialDelay = TimeSpan.FromMilliseconds(300)
                    }
                },
                cancellationToken);
    }

    private sealed class InfiniteSignalWorkflow : IWorkflow<string, string>
    {
        public Task<string> RunAsync(
            WorkflowContext context, string input, CancellationToken cancellationToken) =>
            context.WaitForSignalAsync<string>("wait", "release", cancellationToken: cancellationToken);
    }
}
