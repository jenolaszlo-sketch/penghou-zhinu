using FluentAssertions;
using Penghou.Zhinu;

namespace Penghou.Zhinu.Sqlite.Tests;

/// <summary>
/// Lease-renewal loss semantics: a rejected renewal (confirmed loss) notifies
/// the owner once so it can cooperatively cancel, while transient renewal
/// errors keep the loop alive and fencing stays authoritative at commit.
/// Engine attempt and run loops wire notification to their cancellations.
/// </summary>
public sealed class LeaseLossTests
{
    [Fact]
    public async Task RejectedRenewal_NotifiesOnceAndStops()
    {
        var renewals = 0;
        var notifications = 0;
        await using var renewal = new LeaseRenewal(
            TimeProvider.System,
            TimeSpan.FromMilliseconds(10),
            _ => ValueTask.FromResult(Interlocked.Increment(ref renewals) < 3),
            _ =>
            {
                Interlocked.Increment(ref notifications);
                return ValueTask.CompletedTask;
            });

        await Task.Delay(TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken);

        notifications.Should().Be(1);
        var settled = renewals;
        await Task.Delay(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);
        renewals.Should().Be(settled);
    }

    [Fact]
    public async Task TransientRenewalError_KeepsLoopAliveWithoutNotification()
    {
        var renewals = 0;
        var notifications = 0;
        await using var renewal = new LeaseRenewal(
            TimeProvider.System,
            TimeSpan.FromMilliseconds(10),
            _ => Interlocked.Increment(ref renewals) switch
            {
                <= 2 => throw new InvalidOperationException("transient"),
                _ => ValueTask.FromResult(true)
            },
            _ =>
            {
                Interlocked.Increment(ref notifications);
                return ValueTask.CompletedTask;
            });

        await Task.Delay(TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken);

        notifications.Should().Be(0);
        renewals.Should().BeGreaterThan(3);
    }

    [Fact]
    public async Task Disposal_StopsRenewalLoop()
    {
        var renewals = 0;
        var renewal = new LeaseRenewal(
            TimeProvider.System,
            TimeSpan.FromMilliseconds(10),
            _ => ValueTask.FromResult(Interlocked.Increment(ref renewals) > 0));

        await Task.Delay(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);
        await renewal.DisposeAsync();
        var settled = renewals;
        await Task.Delay(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);

        renewals.Should().Be(settled);
    }
}
