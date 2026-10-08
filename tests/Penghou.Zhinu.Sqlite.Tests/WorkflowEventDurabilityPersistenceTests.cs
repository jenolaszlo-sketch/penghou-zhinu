using FluentAssertions;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;

namespace Penghou.Zhinu.Sqlite.Tests;

/// <summary>
/// The durability classification travels with the persisted event: after a store
/// reopen, durable execution transitions still classify as durable and progress
/// events as advisory. Ordering and export/cursor semantics are unchanged.
/// </summary>
public sealed class WorkflowEventDurabilityPersistenceTests : WorkflowEngineTestBase
{
    [Fact]
    public async Task ClassificationSurvivesReopenAndExportUnchanged()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = Path.Combine(root, "durability.db");
        var workflow = new ProgressWorkflow();
        var store = new SqliteWorkflowStore(new ZhinuSqliteOptions
        {
            DatabasePath = database,
            BusyTimeout = TimeSpan.FromSeconds(2),
            Pooling = false
        });
        await using (var engine = new WorkflowEngine(
            store,
            new WorkflowRegistry().Register("progress", "1", workflow),
            new ZhinuOptions { PollInterval = TimeSpan.FromMilliseconds(10) }))
        {
            var result = await engine.RunAsync<string, string>(
                "progress", "1", "value", cancellationToken: ct);
            result.Should().Be("value-done");
        }

        var runId = workflow.RunId;

        // A fresh store simulates process restart; classification is derived from
        // the persisted event type, so it is stable across reopen.
        var reopened = new SqliteWorkflowStore(new ZhinuSqliteOptions
        {
            DatabasePath = database,
            BusyTimeout = TimeSpan.FromSeconds(2),
            Pooling = false
        });
        var events = await reopened.GetEventsAsync(runId, 0, 1000, ct);
        events.Should().ContainSingle(e =>
            e.EventType == WorkflowEventTypes.WorkflowCompleted &&
            e.Durability == WorkflowEventDurability.Durable);
        events.Where(e => e.EventType == WorkflowEventTypes.Progress)
            .Should().NotBeEmpty()
            .And.OnlyContain(e => e.Durability == WorkflowEventDurability.Advisory);

        // Export/cursor semantics are unchanged: the same durable events, in the
        // same sequence order and identity, with classification attached.
        var batch = await reopened.ReadExportBatchAsync("durability-consumer", runId, 1000, ct);
        batch.Select(e => e.Sequence).Should().BeInAscendingOrder();
        batch.Should().BeEquivalentTo(events, options => options.WithStrictOrdering());
        await reopened.AcknowledgeExportAsync("durability-consumer", runId, batch[^1].Sequence, ct);
        (await reopened.ReadExportBatchAsync("durability-consumer", runId, 1000, ct))
            .Should().BeEmpty();
    }
}
