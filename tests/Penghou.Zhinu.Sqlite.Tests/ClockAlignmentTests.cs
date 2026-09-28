using FluentAssertions;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;
using Penghou.Zhinu.Testing;

namespace Penghou.Zhinu.Sqlite.Tests;

/// <summary>
/// Engine and store clocks stay aligned on the injected provider: persisted
/// timestamps follow the configured clock while scheduling still works.
/// </summary>
public sealed class ClockAlignmentTests
{
    [Fact]
    public async Task InjectedClock_DrivesStoreTimestampsAndScheduling()
    {
        var shift = TimeSpan.FromHours(1);
        var clock = new ShiftedClock(TimeProvider.System, shift);
        await using var host = new ZhinuTestHost(
            new WorkflowRegistry().Register("clock", "1", new EchoWorkflow()),
            configure: null,
            timeProvider: clock);
        var before = DateTimeOffset.UtcNow + shift;
        var runId = await host.Engine.StartAsync(
            "clock", "1", "x", cancellationToken: TestContext.Current.CancellationToken);
        await host.Engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
        var result = await host.Engine.WaitForCompletionAsync<string>(
            runId, cancellationToken: TestContext.Current.CancellationToken);
        var after = DateTimeOffset.UtcNow + shift;

        result.Should().Be("echo:x");
        var run = (await host.Store.GetRunAsync(runId, TestContext.Current.CancellationToken))!;
        run.CreatedAt.Should().BeOnOrAfter(before.AddSeconds(-5)).And.BeOnOrBefore(after.AddSeconds(5));
        var instance = (await host.Store.GetInstanceAsync(
            (await host.Store.GetGenerationByRunAsync(runId, TestContext.Current.CancellationToken))!
                .InstanceId,
            TestContext.Current.CancellationToken))!;
        instance.CreatedAt.Should().BeOnOrAfter(before.AddSeconds(-5)).And.BeOnOrBefore(after.AddSeconds(5));
    }

    private sealed class ShiftedClock(TimeProvider inner, TimeSpan shift) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => inner.GetUtcNow() + shift;

        public override long GetTimestamp() => inner.GetTimestamp();
    }

    private sealed class EchoWorkflow : IWorkflow<string, string>
    {
        public Task<string> RunAsync(
            WorkflowContext context, string input, CancellationToken cancellationToken) =>
            context.StepAsync("echo", _ => Task.FromResult($"echo:{input}"), cancellationToken: cancellationToken);
    }
}
