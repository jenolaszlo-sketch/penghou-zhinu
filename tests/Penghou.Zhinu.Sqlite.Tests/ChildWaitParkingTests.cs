using FluentAssertions;
using Penghou.Zhinu;
using Penghou.Zhinu.Context;
using System.Text.Json;

namespace Penghou.Zhinu.Sqlite.Tests;

/// <summary>
/// When a child run is leased by another owner this worker cannot drive it
/// inline, so the parent parks a child wait and releases capacity. The wait
/// wakes when the child reaches a terminal state.
/// </summary>
public sealed class ChildWaitParkingTests : WorkflowEngineTestBase
{
    [Fact]
    public async Task AwaitChild_LeasedElsewhere_ParksThenResumesWhenReady()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore();
        var now = DateTimeOffset.UtcNow;
        var stringType = SerializationIdentity.TypeId(typeof(string));

        var parentId = await CreateRunAsync(store, "parent", now, parentRunId: null, ct);
        var childId = Guid.NewGuid();
        await store.CreateRunAsync(
            new WorkflowRun
            {
                Id = childId,
                WorkflowName = "child",
                WorkflowVersion = "1",
                Status = WorkflowStatus.Pending,
                CreatedAt = now,
                UpdatedAt = now,
                ParentRunId = parentId,
                InputJson = JsonSerializer.Serialize("x"),
                InputType = stringType,
                OutputType = stringType
            },
            ct);

        // A peer owns the child's lease, so this worker cannot run it inline.
        var generation = (await store.GetRunAsync(parentId, ct))!.LeaseGeneration;
        (await store.TryClaimRunAsync(
            childId, "peer", now, now.AddHours(1), ct)).Should().NotBeNull();

        var coordinator = new ChildRunCoordinator(
            parentId,
            store,
            new ZhinuOptions(),
            ZhinuJsonDefaults.CreateDefault(),
            TimeProvider.System,
            ownerId: "this-worker",
            leaseGeneration: generation,
            executeChildRun: null);

        var stepId = Guid.NewGuid();
        var parked = await coordinator
            .Invoking(value => value.AwaitChildCoreAsync<string>(
                childId, "child:wait", stepRevision: 1, stepId, ct))
            .Should().ThrowAsync<ParkedExecutionException>();

        var wait = (await store.GetWaitAsync(parentId, "child:wait", ct))!;
        wait.Kind.Should().Be(WaitKind.Child);
        wait.Status.Should().Be(WaitStatus.Parked);
        wait.ChildRunId.Should().Be(childId);
        parked.Which.WaitId.Should().Be(wait.WaitId);

        // The child finishes elsewhere; the terminal transition flips the wait.
        await store.CompleteRunAsync(
            childId, "peer", JsonSerializer.Serialize("x"), stringType, now, ct);
        await store.MarkChildWaitsReadyAsync(childId, now, ct);
        (await store.GetWaitAsync(parentId, "child:wait", ct))!.Status
            .Should().Be(WaitStatus.Ready);

        var result = await coordinator.AwaitChildCoreAsync<string>(
            childId, "child:wait", stepRevision: 1, stepId, ct);

        result.Should().Be("x");
        (await store.GetWaitAsync(parentId, "child:wait", ct))!.Status
            .Should().Be(WaitStatus.Completed);
    }
}
