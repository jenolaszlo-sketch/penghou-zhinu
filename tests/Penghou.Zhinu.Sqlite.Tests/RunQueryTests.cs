using FluentAssertions;

namespace Penghou.Zhinu.Sqlite.Tests;

public sealed class RunQueryTests : WorkflowEngineTestBase
{

    [Fact]
    public async Task Store_RejectsSameStepKeyWithDifferentInput()
    {
        var store = CreateStore();
        await store.InitializeAsync(TestContext.Current.CancellationToken);
        var now = DateTimeOffset.UtcNow;
        var runId = Guid.NewGuid();
        await store.CreateRunAsync(
            new WorkflowRun
            {
                Id = runId,
                WorkflowName = "manual",
                WorkflowVersion = "1",
                Status = WorkflowStatus.Pending,
                CreatedAt = now,
                UpdatedAt = now
            },
            TestContext.Current.CancellationToken);
        var generation = await store.TryClaimRunAsync(
            runId,
            "owner",
            now,
            now.AddMinutes(1),
            TestContext.Current.CancellationToken);
        generation.Should().NotBeNull();
        var first = await store.ClaimStepAsync(
            new StepClaimRequest
            {
                WorkflowRunId = runId,
                StepKey = "step",
                InputJson = "1",
                InputType = "int",
                InputHash = "one",
                OutputType = "string",
                OwnerId = "owner",
                Now = now,
                LeaseExpiresAt = now.AddMinutes(1),
                LeaseGeneration = generation!.Value
            },
            TestContext.Current.CancellationToken);
        await store.CompleteStepAsync(
            first.Step.Id,
            "owner",
            "\"ok\"",
            now,
            TestContext.Current.CancellationToken);

        var action = () => store.ClaimStepAsync(new StepClaimRequest
        {
            WorkflowRunId = runId,
            StepKey = "step",
            InputJson = "2",
            InputType = "int",
            InputHash = "two",
            OutputType = "string",
            OwnerId = "owner",
            Now = now,
            LeaseExpiresAt = now.AddMinutes(1),
            LeaseGeneration = generation!.Value
        }, TestContext.Current.CancellationToken).AsTask();

        await action.Should().ThrowAsync<WorkflowStateException>()
            .WithMessage("*incompatible input or result contract*");
    }

    [Fact]
    public async Task GetRunsAsync_FiltersStatusAndWorkflowName()
    {
        var workflow = new TwoStepWorkflow();
        var engine = CreateEngine(workflow, "query");
        var pending = await engine.StartAsync(
            "query",
            "1",
            "value",
            cancellationToken: TestContext.Current.CancellationToken);
        var other = await engine.StartAsync(
            "query",
            "1",
            "value",
            cancellationToken: TestContext.Current.CancellationToken);

        var all = await engine.GetRunsAsync(
            new RunQuery { Limit = 100 },
            TestContext.Current.CancellationToken);
        all.Select(run => run.Id).Should().Contain(new[] { pending, other });

        var onlyPending = await engine.GetRunsAsync(
            new RunQuery { Statuses = new[] { WorkflowStatus.Pending } },
            TestContext.Current.CancellationToken);
        onlyPending.Select(run => run.Id).Should().Contain(new[] { pending, other });

        var byName = await engine.GetRunsAsync(
            new RunQuery { WorkflowName = "missing-name" },
            TestContext.Current.CancellationToken);
        byName.Should().BeEmpty();

        var byVersion = await engine.GetRunsAsync(
            new RunQuery { WorkflowVersion = "1" },
            TestContext.Current.CancellationToken);
        byVersion.Select(run => run.Id).Should().Contain(new[] { pending, other });

        var before = await engine.GetRunsAsync(
            new RunQuery { CreatedBefore = DateTimeOffset.UtcNow.AddMinutes(-1) },
            TestContext.Current.CancellationToken);
        before.Should().BeEmpty();
    }

    [Fact]
    public async Task GetRunsAsync_CursorPaginationIsStable()
    {
        var workflow = new TwoStepWorkflow();
        var engine = CreateEngine(workflow, "cursor");
        var ids = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            ids.Add(await engine.StartAsync(
                "cursor",
                "1",
                $"value{i}",
                cancellationToken: TestContext.Current.CancellationToken));
        }

        var page1 = await engine.GetRunsAsync(
            new RunQuery { Limit = 2 },
            TestContext.Current.CancellationToken);
        var page2 = await engine.GetRunsAsync(
            new RunQuery { AfterId = page1[^1].Id, Limit = 2 },
            TestContext.Current.CancellationToken);
        var page3 = await engine.GetRunsAsync(
            new RunQuery { AfterId = page2[^1].Id, Limit = 2 },
            TestContext.Current.CancellationToken);

        page1.Should().HaveCount(2);
        page2.Should().HaveCount(2);
        page3.Should().HaveCount(1);
        var combined = page1.Concat(page2).Concat(page3)
            .Select(run => run.Id)
            .ToList();
        combined.Should().BeEquivalentTo(ids);
        combined.Distinct().Should().HaveCount(combined.Count);
    }

    [Fact]
    public async Task GetRunSubtreeAsync_ReturnsRootAndDescendantsInCreationOrder()
    {
        var store = CreateStore();
        await store.InitializeAsync(TestContext.Current.CancellationToken);
        var now = DateTimeOffset.UtcNow;
        var root = await CreateRunAsync(
            store,
            "root",
            now.AddSeconds(1),
            parentRunId: null,
            TestContext.Current.CancellationToken);
        var child1 = await CreateRunAsync(
            store,
            "child-1",
            now.AddSeconds(2),
            root,
            TestContext.Current.CancellationToken);
        var grandchild = await CreateRunAsync(
            store,
            "grandchild",
            now.AddSeconds(3),
            child1,
            TestContext.Current.CancellationToken);
        var child2 = await CreateRunAsync(
            store,
            "child-2",
            now.AddSeconds(4),
            root,
            TestContext.Current.CancellationToken);
        await CreateRunAsync(
            store,
            "other",
            now.AddSeconds(5),
            parentRunId: null,
            TestContext.Current.CancellationToken);

        var subtree = await store.GetRunSubtreeAsync(
            root,
            maxDepth: 8,
            TestContext.Current.CancellationToken);

        subtree.Select(run => run.Id).Should().Equal(root, child1, grandchild, child2);
    }

    [Fact]
    public async Task GetRunSubtreeAsync_MaxDepth_LimitsDescendants()
    {
        var store = CreateStore();
        await store.InitializeAsync(TestContext.Current.CancellationToken);
        var now = DateTimeOffset.UtcNow;
        var root = await CreateRunAsync(
            store,
            "root",
            now.AddSeconds(1),
            parentRunId: null,
            TestContext.Current.CancellationToken);
        var child = await CreateRunAsync(
            store,
            "child",
            now.AddSeconds(2),
            root,
            TestContext.Current.CancellationToken);
        await CreateRunAsync(
            store,
            "grandchild",
            now.AddSeconds(3),
            child,
            TestContext.Current.CancellationToken);

        var subtree = await store.GetRunSubtreeAsync(
            root,
            maxDepth: 1,
            TestContext.Current.CancellationToken);

        subtree.Select(run => run.Id).Should().Equal(root, child);
    }

    [Fact]
    public async Task GetRunSubtreeAsync_UnknownRoot_ReturnsEmpty()
    {
        var store = CreateStore();
        await store.InitializeAsync(TestContext.Current.CancellationToken);
        var now = DateTimeOffset.UtcNow;
        await CreateRunAsync(
            store,
            "root",
            now,
            parentRunId: null,
            TestContext.Current.CancellationToken);

        var subtree = await store.GetRunSubtreeAsync(
            Guid.NewGuid(),
            maxDepth: 8,
            TestContext.Current.CancellationToken);

        subtree.Should().BeEmpty();
    }

    [Fact]
    public async Task PurgeRunsAsync_DeletesOldRunsAndCascades()
    {
        var workflow = new TwoStepWorkflow();
        var engine = CreateEngine(workflow, "purge");
        var now = DateTimeOffset.UtcNow;
        var keepId = await engine.StartAsync(
            "purge",
            "1",
            "value",
            cancellationToken: TestContext.Current.CancellationToken);

        var store = CreateStore();
        var oldRunId = Guid.NewGuid();
        await store.CreateRunAsync(
            new WorkflowRun
            {
                Id = oldRunId,
                WorkflowName = "purge",
                WorkflowVersion = "1",
                Status = WorkflowStatus.Completed,
                CreatedAt = now.AddDays(-5),
                UpdatedAt = now.AddDays(-5),
                CompletedAt = now.AddDays(-5)
            },
            TestContext.Current.CancellationToken);

        var deleted = await engine.PurgeRunsAsync(
            now.AddDays(-1),
            new[] { WorkflowStatus.Completed },
            TestContext.Current.CancellationToken);

        deleted.Should().Be(1);
        (await engine.GetRunAsync(
            oldRunId,
            TestContext.Current.CancellationToken)).Should().BeNull();
        (await engine.GetRunAsync(
            keepId,
            TestContext.Current.CancellationToken)).Should().NotBeNull();
    }
}
