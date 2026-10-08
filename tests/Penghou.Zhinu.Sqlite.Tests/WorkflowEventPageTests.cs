using FluentAssertions;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;

namespace Penghou.Zhinu.Sqlite.Tests;

/// <summary>
/// Bounded cursor-based event-page reads over the existing per-run
/// <see cref="WorkflowEvent.Sequence"/> ordering: exclusive cursor, explicit
/// <c>hasMore</c>, durable-sequence tracking that advisory events never advance,
/// a truthful retention floor, and no resync while history is retained. The page
/// read is stateless and independent of durable export acknowledgement.
/// <para>
/// Creating a run emits one durable <c>workflow-started</c> event, so a run
/// always begins with a durable event.
/// </para>
/// </summary>
public sealed class WorkflowEventPageTests : WorkflowEngineTestBase
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private SqliteWorkflowStore Store(string name) => new(new ZhinuSqliteOptions
    {
        DatabasePath = Path.Combine(root, name + ".db"),
        BusyTimeout = TimeSpan.FromSeconds(2),
        Pooling = false
    });

    private static WorkflowEngine EngineOver(IWorkflowStore store) =>
        new(store, new WorkflowRegistry(), new ZhinuOptions { PollInterval = TimeSpan.FromMilliseconds(10) });

    [Fact]
    public async Task EmptyStream_ReturnsEmptyPage()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = Store("empty");
        await using var engine = EngineOver(store);

        // A run that does not exist has no events.
        var page = await engine.GetEventPageAsync(Guid.NewGuid(), 0, 10, ct);

        page.Events.Should().BeEmpty();
        page.NextCursor.Should().Be(0);
        page.HasMore.Should().BeFalse();
        page.ThroughDurableSequence.Should().Be(0);
        page.RetentionFloor.Should().Be(0);
        page.ResyncRequired.Should().BeFalse();
    }

    [Fact]
    public async Task InterleavedDurableAndAdvisory_ComputesThroughDurableSequence()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = Store("through");
        var runId = await CreateRunAsync(store, "w", Now, null, ct);
        // Creation emits workflow-started (durable); add one more durable then two advisory.
        // Stream shape: Durable, Durable, Advisory, Advisory.
        await store.AppendEventAsync(runId, WorkflowEventTypes.StepCompleted, null, cancellationToken: ct);
        await store.AppendEventAsync(runId, WorkflowEventTypes.Progress, "1", cancellationToken: ct);
        await store.AppendEventAsync(runId, WorkflowEventTypes.Progress, "2", cancellationToken: ct);
        await using var engine = EngineOver(store);

        var all = await engine.GetEventPageAsync(runId, 0, 10, ct);
        var sequences = all.Events.Select(e => e.Sequence).ToArray();
        sequences.Should().BeInAscendingOrder();
        all.Events.Should().HaveCount(4);
        all.NextCursor.Should().Be(sequences[^1]);
        all.HasMore.Should().BeFalse();
        all.ThroughDurableSequence.Should().Be(sequences[1]);
        all.ThroughDurableSequence.Should().BeLessThan(all.NextCursor);
        all.RetentionFloor.Should().Be(0);
        all.ResyncRequired.Should().BeFalse();

        // Advisory-only tail page keeps the durable position from earlier pages.
        var tail = await engine.GetEventPageAsync(runId, sequences[1], 10, ct);
        tail.Events.Should().HaveCount(2);
        tail.Events.Should().OnlyContain(e => e.Durability == WorkflowEventDurability.Advisory);
        tail.NextCursor.Should().Be(sequences[^1]);
        tail.ThroughDurableSequence.Should().Be(sequences[1]);
        tail.ResyncRequired.Should().BeFalse();
    }

    [Fact]
    public async Task Boundaries_MultiplePages_PartialFinal_EmptyIncremental()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = Store("boundaries");
        var runId = await CreateRunAsync(store, "w", Now, null, ct);
        for (var index = 0; index < 4; index++)
            await store.AppendEventAsync(runId, WorkflowEventTypes.StepCompleted, index.ToString(), cancellationToken: ct);
        await using var engine = EngineOver(store);

        // Total stream is 5 durable events (creation + 4).
        var exact = await engine.GetEventPageAsync(runId, 0, 5, ct);
        exact.Events.Should().HaveCount(5);
        exact.HasMore.Should().BeFalse();
        exact.ThroughDurableSequence.Should().Be(exact.NextCursor); // durable-only stream

        // Partial page: hasMore is explicit, not inferred from the count.
        var first = await engine.GetEventPageAsync(runId, 0, 2, ct);
        first.Events.Should().HaveCount(2);
        first.HasMore.Should().BeTrue();

        var second = await engine.GetEventPageAsync(runId, first.NextCursor, 2, ct);
        second.Events.Should().HaveCount(2);
        second.HasMore.Should().BeTrue();
        second.NextCursor.Should().BeGreaterThan(first.NextCursor);

        var third = await engine.GetEventPageAsync(runId, second.NextCursor, 2, ct);
        third.Events.Should().HaveCount(1);
        third.HasMore.Should().BeFalse();

        // Empty incremental read: cursor unchanged, no resync.
        var empty = await engine.GetEventPageAsync(runId, third.NextCursor, 2, ct);
        empty.Events.Should().BeEmpty();
        empty.NextCursor.Should().Be(third.NextCursor);
        empty.HasMore.Should().BeFalse();
        empty.ResyncRequired.Should().BeFalse();
    }

    [Fact]
    public async Task CursorIsExclusive_AndMonotonicAcrossPages()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = Store("exclusive");
        var runId = await CreateRunAsync(store, "w", Now, null, ct);
        await store.AppendEventAsync(runId, WorkflowEventTypes.StepCompleted, null, cancellationToken: ct);
        await using var engine = EngineOver(store);

        var all = await engine.GetEventPageAsync(runId, 0, 10, ct);
        var first = all.Events[0].Sequence;
        var second = all.Events[1].Sequence;

        var afterFirst = await engine.GetEventPageAsync(runId, first, 10, ct);
        afterFirst.Events.Select(e => e.Sequence).Should().Equal(second);
        afterFirst.NextCursor.Should().Be(second);
        afterFirst.NextCursor.Should().BeGreaterThanOrEqualTo(first);
    }

    [Fact]
    public async Task ApplicationDefinedType_IsDurable()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = Store("app");
        var runId = await CreateRunAsync(store, "w", Now, null, ct);
        await store.AppendEventAsync(runId, "application.custom", "{}", cancellationToken: ct);
        await using var engine = EngineOver(store);

        var page = await engine.GetEventPageAsync(runId, 0, 10, ct);

        page.Events.Should().Contain(e => e.EventType == "application.custom")
            .Which.Durability.Should().Be(WorkflowEventDurability.Durable);
        page.ThroughDurableSequence.Should().Be(page.NextCursor); // all durable
    }

    [Fact]
    public async Task InvalidCursorOrLimit_Throws()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = Store("invalid");
        var runId = await CreateRunAsync(store, "w", Now, null, ct);
        await using var engine = EngineOver(store);

        await FluentActions.Invoking(() => engine.GetEventPageAsync(runId, -1, 10, ct))
            .Should().ThrowAsync<ArgumentOutOfRangeException>();
        await FluentActions.Invoking(() => engine.GetEventPageAsync(runId, 0, 0, ct))
            .Should().ThrowAsync<ArgumentOutOfRangeException>();
        await FluentActions.Invoking(() => engine.GetEventPageAsync(runId, 0, 1001, ct))
            .Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task PageRead_IsStableAcrossStoreReopen()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = Store("reopen");
        var runId = await CreateRunAsync(store, "w", Now, null, ct);
        await store.AppendEventAsync(runId, WorkflowEventTypes.StepCompleted, null, cancellationToken: ct);
        await store.AppendEventAsync(runId, WorkflowEventTypes.Progress, "1", cancellationToken: ct);
        var expectedThroughDurable =
            (await store.GetEventsAsync(runId, 0, 100, ct))
            .Where(e => e.Durability == WorkflowEventDurability.Durable)
            .Max(e => e.Sequence);

        // A fresh store over the same file simulates process restart.
        var reopened = Store("reopen");
        await using var engine = EngineOver(reopened);
        var page = await engine.GetEventPageAsync(runId, 0, 10, ct);

        page.Events.Select(e => e.EventType).Should().ContainInOrder(
            WorkflowEventTypes.WorkflowStarted, WorkflowEventTypes.StepCompleted, WorkflowEventTypes.Progress);
        page.ThroughDurableSequence.Should().Be(expectedThroughDurable);
        page.HasMore.Should().BeFalse();
    }

    [Fact]
    public async Task PageReadAndExportAcknowledgement_AreIndependent()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = Store("export");
        var runId = await CreateRunAsync(store, "w", Now, null, ct);
        await store.AppendEventAsync(runId, WorkflowEventTypes.StepCompleted, null, cancellationToken: ct);
        await store.AppendEventAsync(runId, WorkflowEventTypes.Progress, "1", cancellationToken: ct);
        await using var engine = EngineOver(store);

        // A stateless page read does not advance the durable export cursor.
        _ = await engine.GetEventPageAsync(runId, 0, 10, ct);
        var export = await store.ReadExportBatchAsync("consumer", runId, 100, ct);
        export.Should().HaveCount(3);

        // Acknowledging export drains the export cursor but never the page reads.
        await store.AcknowledgeExportAsync("consumer", runId, export[^1].Sequence, ct);
        (await store.ReadExportBatchAsync("consumer", runId, 100, ct)).Should().BeEmpty();
        (await engine.GetEventPageAsync(runId, 0, 10, ct)).Events.Should().HaveCount(3);
    }

    [Fact]
    public async Task StoreLevelPageRead_MatchesEngineRead()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = Store("store");
        var runId = await CreateRunAsync(store, "w", Now, null, ct);
        await store.AppendEventAsync(runId, WorkflowEventTypes.StepCompleted, null, cancellationToken: ct);
        await store.AppendEventAsync(runId, WorkflowEventTypes.Progress, "1", cancellationToken: ct);

        var viaStore = await store.ReadEventPageAsync(runId, 0, 10, ct);
        await using var engine = EngineOver(store);
        var viaEngine = await engine.GetEventPageAsync(runId, 0, 10, ct);

        viaStore.Should().BeEquivalentTo(viaEngine);
    }
}
