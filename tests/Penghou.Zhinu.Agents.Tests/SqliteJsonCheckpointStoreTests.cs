using FluentAssertions;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Data.Sqlite;
using Penghou.Zhinu.Sqlite;
using System.Text.Json;

namespace Penghou.Zhinu.Agents.Tests;

public sealed class SqliteJsonCheckpointStoreTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "penghou-zhinu-agents-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Create_And_Retrieve_RoundTripJsonElement()
    {
        var store = CreateStore();
        var payload = JsonDocument.Parse("""{"step":2,"value":"ok"}""").RootElement;

        var key = await store.CreateCheckpointAsync("session-1", payload);

        key.SessionId.Should().Be("session-1");
        var retrieved = await store.RetrieveCheckpointAsync("session-1", key);
        retrieved.GetRawText().Should().Be(payload.GetRawText());
    }

    [Fact]
    public async Task Create_GeneratesUniqueCheckpointIds()
    {
        var store = CreateStore();
        var payload = JsonDocument.Parse("{}").RootElement;

        var first = await store.CreateCheckpointAsync("session-1", payload);
        var second = await store.CreateCheckpointAsync("session-1", payload);

        first.CheckpointId.Should().NotBe(second.CheckpointId);
    }

    [Fact]
    public async Task Retrieve_MissingCheckpoint_ThrowsKeyNotFound()
    {
        var store = CreateStore();

        var action = async () => await store.RetrieveCheckpointAsync(
            "session-1",
            new CheckpointInfo("session-1", "does-not-exist"));

        await action.Should().ThrowAsync<WorkflowNotFoundException>();
    }

    [Fact]
    public async Task RetrieveIndex_IsScopedBySession()
    {
        var store = CreateStore();
        var payload = JsonDocument.Parse("{}").RootElement;
        await store.CreateCheckpointAsync("session-a", payload);
        await store.CreateCheckpointAsync("session-b", payload);

        var index = await store.RetrieveIndexAsync("session-a");

        index.Should().ContainSingle()
            .Which.SessionId.Should().Be("session-a");
    }

    [Fact]
    public async Task RetrieveIndex_ReturnsMostRecentFirst()
    {
        var store = CreateStore();
        var payload = JsonDocument.Parse("{}").RootElement;
        var first = await store.CreateCheckpointAsync("session-1", payload);
        var second = await store.CreateCheckpointAsync("session-1", payload);
        var third = await store.CreateCheckpointAsync("session-1", payload);

        var index = await store.RetrieveIndexAsync("session-1");

        index.Select(item => item.CheckpointId).Should().Equal(
            third.CheckpointId,
            second.CheckpointId,
            first.CheckpointId);
    }

    [Fact]
    public async Task RetrieveIndex_FiltersByParent()
    {
        var store = CreateStore();
        var payload = JsonDocument.Parse("{}").RootElement;
        var root = await store.CreateCheckpointAsync("session-1", payload);
        await store.CreateCheckpointAsync("session-1", payload, root);
        await store.CreateCheckpointAsync("session-1", payload);

        var children = await store.RetrieveIndexAsync("session-1", root);

        children.Should().ContainSingle()
            .Which.CheckpointId.Should().NotBe(root.CheckpointId);
    }

    [Fact]
    public async Task Checkpoints_SurviveNewStoreInstance()
    {
        var payload = JsonDocument.Parse("""{"kept":true}""").RootElement;
        await CreateStore().CreateCheckpointAsync("session-1", payload);

        var reopened = CreateStore();
        var index = await reopened.RetrieveIndexAsync("session-1");

        index.Should().ContainSingle();
        var retrieved = await reopened.RetrieveCheckpointAsync("session-1", index.Single());
        retrieved.GetProperty("kept").GetBoolean().Should().BeTrue();
    }

    private SqliteJsonCheckpointStore CreateStore() =>
        new(new ZhinuSqliteOptions
        {
            DatabasePath = Path.Combine(root, "maf-checkpoints.db"),
            BusyTimeout = TimeSpan.FromSeconds(2)
        });

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }
}
