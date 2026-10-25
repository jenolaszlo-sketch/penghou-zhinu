using System.Text.Json;
using FluentAssertions;

namespace Penghou.Zhinu.Sqlite.Tests;

public sealed class ActivityGroupQueryGenerationTests(ITestOutputHelper output) : WorkflowEngineTestBase
{
    [Fact]
    public async Task GenerationAndDispositionChangesDoNotReinterpretCachedPagesAtSameWatermark()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore();
        var runId = await CreateRunAsync(store, "generation-query", DateTimeOffset.UtcNow, null, ct);
        var instance = await store.CreateInstanceAsync(null, ct);
        var generation = await store.CreateGenerationAsync(instance.InstanceId, runId, "private-plan", "private-fingerprint", null, ct);
        using var reader = new WorkflowGroupReader(store);
        var first = await reader.GetRunGroupSnapshotAsync(runId, ct);
        first.Status.Should().Be(WorkflowGroupReadStatus.Success, first.Reason);
        first.Value!.Generation!.GenerationId.Should().Be(generation.GenerationId);
        first.Value.Dispositions.Should().BeEmpty();

        await store.RecordDispositionAsync(generation.GenerationId, CheckpointDisposition.Accept, "private-reason", "private-actor", ct);
        var second = await reader.GetRunGroupSnapshotAsync(runId, ct);
        second.Status.Should().Be(WorkflowGroupReadStatus.Success, second.Reason);
        second.Value!.ThroughDurableSequence.Should().Be(first.Value.ThroughDurableSequence);
        second.Value.SnapshotToken.Should().NotBe(first.Value.SnapshotToken);
        second.Value.Dispositions.Should().ContainSingle().Which.Disposition.Should().Be(CheckpointDisposition.Accept);
        first.Value.Dispositions.Should().BeEmpty();
        var oldPage = await reader.QueryGroupChildrenAsync(runId, first.Value.SnapshotToken, cancellationToken: ct);
        oldPage.Status.Should().Be(WorkflowGroupReadStatus.Success);
        oldPage.Value!.ThroughDurableSequence.Should().Be(first.Value.ThroughDurableSequence);
        JsonSerializer.Serialize(second.Value).Should().NotContain("private-");
        output.WriteLine("QUERY_EVIDENCE " + JsonSerializer.Serialize(new { scenario = "snapshot-only-generation-disposition", first = first.Value, second = second.Value, oldPage = oldPage.Value }));
    }
}
