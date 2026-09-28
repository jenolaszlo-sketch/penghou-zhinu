using FluentAssertions;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;

namespace Penghou.Zhinu.Sqlite.Tests;

/// <summary>
/// Durable event export: bounded cursor reads, idempotent monotonic acks,
/// replay after a crash before acknowledgement, and retention that skips
/// runs with lagging consumers.
/// </summary>
public sealed class EventExportTests : WorkflowEngineTestBase
{
    [Fact]
    public async Task Export_AckAdvancesCursorIdempotently()
    {
        var ct = TestContext.Current.CancellationToken;
        var engine = CreateEngine(new TwoStepWorkflow(), "export-ack");
        var store = PeerStore();
        var runId = await engine.StartAsync("export-ack", "1", "x", cancellationToken: ct);
        await engine.ExecuteAsync(runId, ct);
        await engine.WaitForCompletionAsync<string>(runId, cancellationToken: ct);

        var first = await store.ReadExportBatchAsync("sink", runId, 2, ct);
        first.Should().HaveCount(2);
        first.Select(e => e.Sequence).Should().BeInAscendingOrder();
        await store.AcknowledgeExportAsync("sink", runId, first[^1].Sequence, ct);

        var second = await store.ReadExportBatchAsync("sink", runId, 100, ct);
        second.Should().NotBeEmpty();
        second.All(e => e.Sequence > first[^1].Sequence).Should().BeTrue();

        // Re-acknowledging an older sequence never moves the cursor back.
        await store.AcknowledgeExportAsync("sink", runId, first[0].Sequence, ct);
        var replayed = await store.ReadExportBatchAsync("sink", runId, 100, ct);
        replayed.Select(e => e.Sequence).Should().Equal(second.Select(e => e.Sequence));
    }

    [Fact]
    public async Task Export_CrashBeforeAck_Replays()
    {
        var ct = TestContext.Current.CancellationToken;
        var engine = CreateEngine(new TwoStepWorkflow(), "export-crash");
        var runId = await engine.StartAsync("export-crash", "1", "x", cancellationToken: ct);
        await engine.ExecuteAsync(runId, ct);
        await engine.WaitForCompletionAsync<string>(runId, cancellationToken: ct);

        var store = PeerStore();
        var before = await store.ReadExportBatchAsync("sink", runId, 100, ct);
        before.Should().NotBeEmpty();

        // Simulate exporter crash after sending but before acknowledgement:
        // reopen the store and read again.
        var reopened = PeerStore();
        var after = await reopened.ReadExportBatchAsync("sink", runId, 100, ct);

        after.Select(e => e.Sequence).Should().Equal(before.Select(e => e.Sequence));
        await reopened.AcknowledgeExportAsync("sink", runId, after[^1].Sequence, ct);
        (await reopened.ReadExportBatchAsync("sink", runId, 100, ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task Retention_SkipsLaggingConsumers()
    {
        var ct = TestContext.Current.CancellationToken;
        var engine = CreateEngine(new TwoStepWorkflow(), "export-retention");
        var store = PeerStore();
        var runId = await engine.StartAsync("export-retention", "1", "x", cancellationToken: ct);
        await engine.ExecuteAsync(runId, ct);
        await engine.WaitForCompletionAsync<string>(runId, cancellationToken: ct);
        var events = await store.ReadExportBatchAsync("sink", runId, 1, ct);
        events.Should().HaveCount(1);
        await store.AcknowledgeExportAsync("sink", runId, events[^1].Sequence, ct);
        var options = new RunRetentionOptions
        {
            OlderThan = DateTimeOffset.UtcNow.AddSeconds(5)
        };

        // The consumer lagged after one event: the completed run survives.
        await Task.Delay(TimeSpan.FromMilliseconds(100), ct);
        (await store.PreviewRetentionAsync(options, ct)).EligibleRunCount.Should().Be(0);
        (await store.PurgeRetainedRunsAsync(options, ct)).Should().Be(0);

        var rest = await store.ReadExportBatchAsync("sink", runId, 100, ct);
        await store.AcknowledgeExportAsync("sink", runId, rest[^1].Sequence, ct);
        (await store.PreviewRetentionAsync(options, ct)).EligibleRunCount.Should().Be(1);
        (await store.PurgeRetainedRunsAsync(options, ct)).Should().Be(1);
        (await store.GetRunAsync(runId, ct)).Should().BeNull();

        // Acknowledging a purged run is a no-op success, not a crash loop.
        await store.AcknowledgeExportAsync("sink", runId, rest[^1].Sequence, ct);
        await store.AcknowledgeExportAsync("sink", Guid.NewGuid(), 0, ct);
    }

    private SqliteWorkflowStore PeerStore() =>
        new(new ZhinuSqliteOptions
        {
            DatabasePath = Path.Combine(root, "zhinu.db"),
            BusyTimeout = TimeSpan.FromSeconds(2),
            Pooling = false
        });
}
