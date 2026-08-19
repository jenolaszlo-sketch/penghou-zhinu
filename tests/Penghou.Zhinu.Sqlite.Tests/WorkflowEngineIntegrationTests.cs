using FluentAssertions;
using Microsoft.Data.Sqlite;

namespace Penghou.Zhinu.Sqlite.Tests;

public sealed class WorkflowEngineIntegrationTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "penghou-zhinu-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task RunAsync_PersistsOutputStepsAndOrderedEvents()
    {
        var workflow = new TwoStepWorkflow();
        var engine = CreateEngine(workflow, "two-step");

        var result = await engine.RunAsync<string, string>(
            "two-step",
            "1",
            "Jeno",
            cancellationToken: TestContext.Current.CancellationToken);

        result.Should().Be("Hello, Jeno!");
        workflow.FirstCalls.Should().Be(1);
        workflow.SecondCalls.Should().Be(1);
        var runId = workflow.RunId;
        var run = await engine.GetRunAsync(
            runId,
            TestContext.Current.CancellationToken);
        run!.Status.Should().Be(WorkflowStatus.Completed);
        (await engine.GetStepsAsync(
            runId,
            TestContext.Current.CancellationToken)).Should().HaveCount(2)
            .And.OnlyContain(step => step.Status == StepStatus.Completed);
        var events = await engine.GetEventsAsync(
            runId,
            cancellationToken: TestContext.Current.CancellationToken);
        events.Select(item => item.Sequence).Should().BeInAscendingOrder();
        events.Should().Contain(item =>
            item.EventType == WorkflowEventTypes.WorkflowCompleted);
    }

    [Fact]
    public async Task ExecuteAsync_ReusesCompletedStepAfterBoundaryReconstruction()
    {
        var firstWorkflow = new RecoveringWorkflow(blockSecond: true);
        var firstEngine = CreateEngine(
            firstWorkflow,
            "recover",
            leaseDuration: TimeSpan.FromMilliseconds(120));
        var runId = await firstEngine.StartAsync(
            "recover",
            "1",
            "input",
            cancellationToken: TestContext.Current.CancellationToken);
        using var interruption = new CancellationTokenSource();
        var execution = firstEngine.ExecuteAsync(runId, interruption.Token);
        await firstWorkflow.SecondStarted.Task.WaitAsync(
            TestContext.Current.CancellationToken);
        await interruption.CancelAsync();
        await execution;
        await Task.Delay(180, TestContext.Current.CancellationToken);

        var resumedWorkflow = new RecoveringWorkflow(blockSecond: false);
        var resumedEngine = CreateEngine(
            resumedWorkflow,
            "recover",
            leaseDuration: TimeSpan.FromMilliseconds(120));
        await resumedEngine.ExecuteAsync(
            runId,
            TestContext.Current.CancellationToken);

        var result = await resumedEngine.WaitForCompletionAsync<string>(
            runId,
            cancellationToken: TestContext.Current.CancellationToken);
        result.Should().Be("first-second");
        firstWorkflow.FirstCalls.Should().Be(1);
        resumedWorkflow.FirstCalls.Should().Be(0);
        resumedWorkflow.SecondCalls.Should().Be(1);
        (await resumedEngine.GetEventsAsync(
            runId,
            cancellationToken: TestContext.Current.CancellationToken)).Should().Contain(item =>
            item.EventType == WorkflowEventTypes.LeaseRecovered);
    }

    [Fact]
    public async Task StepAsync_RetriesAndPersistsAttemptHistory()
    {
        var workflow = new RetryWorkflow();
        var engine = CreateEngine(workflow, "retry");

        var result = await engine.RunAsync<string, string>(
            "retry",
            "1",
            "value",
            cancellationToken: TestContext.Current.CancellationToken);

        result.Should().Be("value-ok");
        workflow.Calls.Should().Be(2);
        var steps = await engine.GetStepsAsync(
            workflow.RunId,
            TestContext.Current.CancellationToken);
        steps.Single().Attempt.Should().Be(2);
        var events = await engine.GetEventsAsync(
            workflow.RunId,
            cancellationToken: TestContext.Current.CancellationToken);
        events.Should().Contain(item => item.EventType == WorkflowEventTypes.StepFailed);
        events.Should().Contain(item => item.EventType == WorkflowEventTypes.RetryScheduled);
    }

    [Fact]
    public async Task StepAsync_PersistsTerminalFailure()
    {
        var workflow = new FailingWorkflow();
        var engine = CreateEngine(workflow, "fail");
        var runId = await engine.StartAsync(
            "fail",
            "1",
            "value",
            cancellationToken: TestContext.Current.CancellationToken);

        await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);

        var action = () => engine.WaitForCompletionAsync<string>(
            runId,
            cancellationToken: TestContext.Current.CancellationToken);
        await action.Should().ThrowAsync<WorkflowExecutionFailedException>()
            .WithMessage("*planned failure*");
        var step = (await engine.GetStepsAsync(
            runId,
            TestContext.Current.CancellationToken)).Single();
        step.Status.Should().Be(StepStatus.Failed);
        step.Error!.StackTrace.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task DelayAsync_PersistsWaitingBoundaryAndCompletes()
    {
        var workflow = new DelayWorkflow();
        var engine = CreateEngine(workflow, "delay");

        var result = await engine.RunAsync<string, string>(
            "delay",
            "1",
            "done",
            cancellationToken: TestContext.Current.CancellationToken);

        result.Should().Be("done");
        var events = await engine.GetEventsAsync(
            workflow.RunId,
            cancellationToken: TestContext.Current.CancellationToken);
        events.Should().Contain(item => item.EventType == WorkflowEventTypes.DelayScheduled);
    }

    [Fact]
    public async Task ParallelCallsWithSameKey_InvokeDelegateOnce()
    {
        var workflow = new ParallelSameKeyWorkflow();
        var engine = CreateEngine(workflow, "parallel");

        var result = await engine.RunAsync<string, string>(
            "parallel",
            "1",
            "value",
            cancellationToken: TestContext.Current.CancellationToken);

        result.Should().Be("value:value");
        workflow.Calls.Should().Be(1);
    }

    [Fact]
    public async Task CancelAsync_CancelsPendingRunDurably()
    {
        var workflow = new TwoStepWorkflow();
        var engine = CreateEngine(workflow, "cancel");
        var runId = await engine.StartAsync(
            "cancel",
            "1",
            "value",
            cancellationToken: TestContext.Current.CancellationToken);

        await engine.CancelAsync(runId, TestContext.Current.CancellationToken);
        await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);

        (await engine.GetRunAsync(
            runId,
            TestContext.Current.CancellationToken))!.Status.Should().Be(WorkflowStatus.Cancelled);
        workflow.FirstCalls.Should().Be(0);
    }

    [Fact]
    public async Task StartAsync_WithSameCallerIdAndInput_IsIdempotent()
    {
        var workflow = new TwoStepWorkflow();
        var engine = CreateEngine(workflow, "idempotent");
        var requestedId = Guid.NewGuid();

        var first = await engine.StartAsync(
            "idempotent",
            "1",
            "value",
            requestedId,
            cancellationToken: TestContext.Current.CancellationToken);
        var second = await engine.StartAsync(
            "idempotent",
            "1",
            "value",
            requestedId,
            cancellationToken: TestContext.Current.CancellationToken);

        second.Should().Be(first);
        var events = await engine.GetEventsAsync(
            first,
            cancellationToken: TestContext.Current.CancellationToken);
        events.Count(item =>
            item.EventType == WorkflowEventTypes.WorkflowStarted).Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_ConcurrentCallsClaimWorkflowOnce()
    {
        var workflow = new SlowWorkflow();
        var engine = CreateEngine(workflow, "single-claim");
        var runId = await engine.StartAsync(
            "single-claim",
            "1",
            "value",
            cancellationToken: TestContext.Current.CancellationToken);

        await Task.WhenAll(
            engine.ExecuteAsync(runId, TestContext.Current.CancellationToken),
            engine.ExecuteAsync(runId, TestContext.Current.CancellationToken));

        workflow.Calls.Should().Be(1);
        (await engine.GetRunAsync(
            runId,
            TestContext.Current.CancellationToken))!.Status
            .Should().Be(WorkflowStatus.Completed);
    }

    [Fact]
    public async Task StepTimeout_IsPersistedAsFailure()
    {
        var workflow = new TimeoutWorkflow();
        var engine = CreateEngine(workflow, "timeout");
        var runId = await engine.StartAsync(
            "timeout",
            "1",
            "value",
            cancellationToken: TestContext.Current.CancellationToken);

        await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);

        var step = (await engine.GetStepsAsync(
            runId,
            TestContext.Current.CancellationToken)).Single();
        step.Status.Should().Be(StepStatus.Failed);
        step.Error!.Type.Should().Be(typeof(TimeoutException).FullName);
    }

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
    public async Task StartAsync_WithDeadline_PersistsIt()
    {
        var workflow = new TwoStepWorkflow();
        var engine = CreateEngine(workflow, "deadline-persist");
        var deadline = DateTimeOffset.UtcNow.AddHours(1);

        var runId = await engine.StartAsync(
            "deadline-persist",
            "1",
            "value",
            deadline: deadline,
            cancellationToken: TestContext.Current.CancellationToken);

        var run = await engine.GetRunAsync(
            runId,
            TestContext.Current.CancellationToken);
        run!.Deadline.Should().Be(deadline);
    }

    [Fact]
    public async Task ExecuteAsync_AfterDeadline_FailsRunWithTimeout()
    {
        var workflow = new TwoStepWorkflow();
        var engine = CreateEngine(workflow, "deadline-passed");
        var runId = await engine.StartAsync(
            "deadline-passed",
            "1",
            "value",
            deadline: DateTimeOffset.UtcNow.AddMinutes(-1),
            cancellationToken: TestContext.Current.CancellationToken);

        await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);

        var run = await engine.GetRunAsync(
            runId,
            TestContext.Current.CancellationToken);
        run!.Status.Should().Be(WorkflowStatus.Failed);
        run.Error!.Type.Should().Be(typeof(TimeoutException).FullName);
        workflow.FirstCalls.Should().Be(0);
    }

    [Fact]
    public async Task EmitAsync_PersistsReplayableProgressEvents()
    {
        var workflow = new ProgressWorkflow();
        var engine = CreateEngine(workflow, "progress");

        var result = await engine.RunAsync<string, string>(
            "progress",
            "1",
            "value",
            cancellationToken: TestContext.Current.CancellationToken);

        result.Should().Be("value-done");
        var events = await engine.GetEventsAsync(
            workflow.RunId,
            cancellationToken: TestContext.Current.CancellationToken);
        events.Where(item => item.EventType == WorkflowEventTypes.Progress)
            .Select(item => item.DataJson)
            .Should().ContainInOrder("25", "50", "75");
    }

    [Fact]
    public async Task StartAsync_WithMetadata_PersistsAndSurvivesRestart()
    {
        var workflow = new TwoStepWorkflow();
        var engine = CreateEngine(workflow, "metadata");
        var runId = await engine.StartAsync(
            "metadata",
            "1",
            "value",
            metadata: new { CorrelationId = "abc", Owner = "tester" },
            cancellationToken: TestContext.Current.CancellationToken);

        var run = await engine.GetRunAsync(
            runId,
            TestContext.Current.CancellationToken);
        run!.MetadataJson.Should().Contain("\"correlationId\":\"abc\"");

        var updated = await engine.UpdateRunMetadataAsync(
            runId,
            new { CorrelationId = "def", Owner = "tester" },
            TestContext.Current.CancellationToken);
        updated!.MetadataJson.Should().Contain("\"correlationId\":\"def\"");
        (await engine.GetRunAsync(
            runId,
            TestContext.Current.CancellationToken))!.MetadataJson
            .Should().Contain("\"correlationId\":\"def\"");
    }

    [Fact]
    public async Task StartAsync_WithMetadata_DoesNotAffectIdempotency()
    {
        var workflow = new TwoStepWorkflow();
        var engine = CreateEngine(workflow, "metadata-idempotent");
        var requestedId = Guid.NewGuid();
        var first = await engine.StartAsync(
            "metadata-idempotent",
            "1",
            "value",
            requestedId,
            metadata: new { Owner = "one" },
            cancellationToken: TestContext.Current.CancellationToken);
        var second = await engine.StartAsync(
            "metadata-idempotent",
            "1",
            "value",
            requestedId,
            metadata: new { Owner = "two" },
            cancellationToken: TestContext.Current.CancellationToken);

        second.Should().Be(first);
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

    [Fact]
    public async Task RestartStepAsync_RerunsStepAndSubtreeWhileReusingPrefix()
    {
        var first = new RestartableWorkflow { SecondSuffix = "a" };
        var engine = CreateEngine(first, "restart");
        var result = await engine.RunAsync<string, string>(
            "restart",
            "1",
            "x",
            cancellationToken: TestContext.Current.CancellationToken);

        result.Should().Be("hello-x-a");
        first.FirstCalls.Should().Be(1);
        first.SecondCalls.Should().Be(1);
        var runId = first.RunId;

        var plan = await engine.RestartStepAsync(
            runId,
            "second",
            TestContext.Current.CancellationToken);
        plan.StepsToInvalidate.Should().ContainSingle();
        plan.StepsToInvalidate.Single().StepKey.Should().Be("second");
        plan.StepsToInvalidate.Single().Reason.Should().Be(RestartReason.Requested);

        var resumed = new RestartableWorkflow { SecondSuffix = "b" };
        var resumedEngine = CreateEngine(resumed, "restart");
        await resumedEngine.ExecuteAsync(
            runId,
            TestContext.Current.CancellationToken);
        var rerun = await resumedEngine.WaitForCompletionAsync<string>(
            runId,
            cancellationToken: TestContext.Current.CancellationToken);

        rerun.Should().Be("hello-x-b");
        first.FirstCalls.Should().Be(1);
        resumed.FirstCalls.Should().Be(0);
        resumed.SecondCalls.Should().Be(1);
        (await resumedEngine.GetStepsAsync(
            runId,
            TestContext.Current.CancellationToken)).Should().HaveCount(2)
            .And.OnlyContain(step => step.Status == StepStatus.Completed);
        (await resumedEngine.GetEventsAsync(
            runId,
            cancellationToken: TestContext.Current.CancellationToken)).Should().Contain(item =>
            item.EventType == WorkflowEventTypes.StepRestarted);
    }

    [Fact]
    public async Task RestartStepAsync_UnknownStep_ThrowsKeyNotFound()
    {
        var workflow = new TwoStepWorkflow();
        var engine = CreateEngine(workflow, "restart-missing");
        var runId = await engine.StartAsync(
            "restart-missing",
            "1",
            "value",
            cancellationToken: TestContext.Current.CancellationToken);

        await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);

        await engine.Invoking(engine =>
            engine.RestartStepAsync(
                runId,
                "missing",
                TestContext.Current.CancellationToken))
            .Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task WaitForSignalAsync_SignalDeliveredAfterWait_CompletesWorkflow()
    {
        var workflow = new SignalWorkflow();
        var engine = CreateEngine(workflow, "signal");
        var runId = await engine.StartAsync(
            "signal",
            "1",
            "x",
            cancellationToken: TestContext.Current.CancellationToken);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var execution = engine.ExecuteAsync(runId, cts.Token);
        await WaitUntilAsync(
            () => HasStepStatusAsync(
                engine,
                runId,
                "approval",
                StepStatus.Waiting,
                cts.Token),
            cts.Token);
        await engine.SendSignalAsync(runId, "approve", "yes", cts.Token);
        await execution;

        var result = await engine.WaitForCompletionAsync<string>(
            runId,
            cancellationToken: TestContext.Current.CancellationToken);
        result.Should().Be("yes");
        (await engine.GetRunAsync(
            runId,
            TestContext.Current.CancellationToken))!.Status.Should().Be(WorkflowStatus.Completed);
    }

    [Fact]
    public async Task WaitForSignalAsync_SignalBufferedBeforeWait_IsDeliveredImmediately()
    {
        var workflow = new SignalWorkflow();
        var engine = CreateEngine(workflow, "signal-buffered");
        var runId = await engine.StartAsync(
            "signal-buffered",
            "1",
            "x",
            cancellationToken: TestContext.Current.CancellationToken);

        await engine.SendSignalAsync(
            runId,
            "approve",
            "yes",
            TestContext.Current.CancellationToken);
        await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);

        var result = await engine.WaitForCompletionAsync<string>(
            runId,
            cancellationToken: TestContext.Current.CancellationToken);
        result.Should().Be("yes");
        var events = await engine.GetEventsAsync(
            runId,
            cancellationToken: TestContext.Current.CancellationToken);
        events.Should().Contain(item => item.EventType == WorkflowEventTypes.SignalSent);
        events.Should().Contain(item => item.EventType == WorkflowEventTypes.SignalDelivered);
    }

    [Fact]
    public async Task WaitForSignalAsync_Timeout_FailsRun()
    {
        var workflow = new SignalWorkflow
        {
            WaitTimeout = TimeSpan.FromMilliseconds(150)
        };
        var engine = CreateEngine(workflow, "signal-timeout");
        var runId = await engine.StartAsync(
            "signal-timeout",
            "1",
            "x",
            cancellationToken: TestContext.Current.CancellationToken);

        await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);

        var action = () => engine.WaitForCompletionAsync<string>(
            runId,
            cancellationToken: TestContext.Current.CancellationToken);
        await action.Should().ThrowAsync<WorkflowExecutionFailedException>()
            .WithMessage("*not delivered before the wait deadline*");
        (await engine.GetRunAsync(
            runId,
            TestContext.Current.CancellationToken))!.Status.Should().Be(WorkflowStatus.Failed);
        (await engine.GetStepsAsync(
            runId,
            TestContext.Current.CancellationToken)).Single(item => item.StepKey == "approval")
            .Status.Should().Be(StepStatus.Waiting);
    }

    [Fact]
    public async Task WaitForSignalAsync_SurvivesInterruption_ThenReceivesLateSignal()
    {
        var workflow = new SignalWorkflow();
        var engine = CreateEngine(workflow, "signal-interrupt");
        var runId = await engine.StartAsync(
            "signal-interrupt",
            "1",
            "x",
            cancellationToken: TestContext.Current.CancellationToken);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var interruption = new CancellationTokenSource();
        var execution = engine.ExecuteAsync(runId, interruption.Token);
        await WaitUntilAsync(
            () => HasStepStatusAsync(
                engine,
                runId,
                "approval",
                StepStatus.Waiting,
                cts.Token),
            cts.Token);
        await interruption.CancelAsync();
        await execution;
        (await engine.GetRunAsync(
            runId,
            TestContext.Current.CancellationToken))!.Status.Should().Be(WorkflowStatus.Running);
        (await engine.GetStepsAsync(
            runId,
            TestContext.Current.CancellationToken)).Single(item => item.StepKey == "approval")
            .Status.Should().Be(StepStatus.Waiting);

        await engine.SendSignalAsync(
            runId,
            "approve",
            "yes",
            TestContext.Current.CancellationToken);
        await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
        var result = await engine.WaitForCompletionAsync<string>(
            runId,
            cancellationToken: TestContext.Current.CancellationToken);
        result.Should().Be("yes");
    }

    [Fact]
    public async Task StartChildAsync_ExecutesChildAndWaitsForResult()
    {
        var parent = new ParentWorkflow();
        var child = new ChildWorkflow();
        var engine = CreateEngine(
            new WorkflowRegistry()
                .Register("parent", "1", parent)
                .Register("child", "1", child));
        var runId = await engine.StartAsync(
            "parent",
            "1",
            "go",
            cancellationToken: TestContext.Current.CancellationToken);

        await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
        var result = await engine.WaitForCompletionAsync<string>(
            runId,
            cancellationToken: TestContext.Current.CancellationToken);

        result.Should().Be("child:parent:go");
        child.Calls.Should().Be(1);
        var childRun = (await engine.GetRunsAsync(
            new RunQuery { WorkflowName = "child" },
            cancellationToken: TestContext.Current.CancellationToken)).Single();
        childRun.Status.Should().Be(WorkflowStatus.Completed);
        childRun.ParentRunId.Should().Be(runId);
        (await engine.GetStepsAsync(
            runId,
            TestContext.Current.CancellationToken)).Should().HaveCount(3)
            .And.OnlyContain(step => step.Status == StepStatus.Completed);
    }

    [Fact]
    public async Task StartChildAsync_ChildFailure_PropagatesToParent()
    {
        var parent = new FailingParentWorkflow();
        var child = new FailingChildWorkflow();
        var engine = CreateEngine(
            new WorkflowRegistry()
                .Register("fail-parent", "1", parent)
                .Register("bad-child", "1", child));
        var runId = await engine.StartAsync(
            "fail-parent",
            "1",
            "x",
            cancellationToken: TestContext.Current.CancellationToken);

        await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);

        var action = () => engine.WaitForCompletionAsync<string>(
            runId,
            cancellationToken: TestContext.Current.CancellationToken);
        await action.Should().ThrowAsync<WorkflowExecutionFailedException>()
            .WithMessage("*child failed*");
        (await engine.GetRunsAsync(
            new RunQuery { WorkflowName = "bad-child" },
            cancellationToken: TestContext.Current.CancellationToken)).Single()
            .Status.Should().Be(WorkflowStatus.Failed);
        (await engine.GetRunAsync(
            runId,
            TestContext.Current.CancellationToken))!.Status.Should().Be(WorkflowStatus.Failed);
    }

    [Fact]
    public async Task StartChildAsync_RestartingStartStepReusesExistingChildRun()
    {
        var parent = new ParentWorkflow();
        var child = new ChildWorkflow();
        var engine = CreateEngine(
            new WorkflowRegistry()
                .Register("parent", "1", parent)
                .Register("child", "1", child));
        var runId = await engine.StartAsync(
            "parent",
            "1",
            "go",
            cancellationToken: TestContext.Current.CancellationToken);
        await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
        var original = await engine.WaitForCompletionAsync<string>(
            runId,
            cancellationToken: TestContext.Current.CancellationToken);
        original.Should().Be("child:parent:go");

        await engine.RestartStepAsync(
            runId,
            "child:start",
            TestContext.Current.CancellationToken);
        await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
        var rerun = await engine.WaitForCompletionAsync<string>(
            runId,
            cancellationToken: TestContext.Current.CancellationToken);
        rerun.Should().Be("child:parent:go");
        var runs = await engine.GetRunsAsync(
            new RunQuery { WorkflowName = "child" },
            cancellationToken: TestContext.Current.CancellationToken);
        runs.Should().ContainSingle()
            .Which.ParentRunId.Should().Be(runId);
    }

    [Fact]
    public async Task RestartStepAsync_DependentsMode_InvalidatesOnlyTransitiveDependents()
    {
        var workflow = new DependentStepsWorkflow();
        var engine = CreateEngine(workflow, "deps");
        var result = await engine.RunAsync<string, string>(
            "deps",
            "1",
            "x",
            cancellationToken: TestContext.Current.CancellationToken);

        result.Should().Be("D[B[A(x)]]|E[C(x)]");
        workflow.ACalls.Should().Be(1);
        workflow.BCalls.Should().Be(1);
        workflow.CCalls.Should().Be(1);
        workflow.DCalls.Should().Be(1);
        workflow.ECalls.Should().Be(1);
        var runId = workflow.RunId;

        var graph = await engine.GetDependencyGraphAsync(
            runId,
            TestContext.Current.CancellationToken);
        graph.Select(item => $"{item.StepKey}->{item.DependsOnStepKey}")
            .Should().BeEquivalentTo("b->a", "d->b", "e->c");

        var plan = await engine.PlanRestartAsync(
            runId,
            "b",
            cancellationToken: TestContext.Current.CancellationToken);
        plan.StepsToInvalidate.Select(item => item.StepKey)
            .Should().Equal("b", "d");
        plan.StepsToInvalidate.Should().Contain(item =>
            item.StepKey == "b" && item.Reason == RestartReason.Requested);
        plan.StepsToInvalidate.Should().Contain(item =>
            item.StepKey == "d" && item.Reason == RestartReason.Dependent);

        var applied = await engine.RestartStepAsync(
            runId,
            "b",
            cancellationToken: TestContext.Current.CancellationToken);
        applied.StepsToInvalidate.Select(item => item.StepKey)
            .Should().Equal("b", "d");

        var resumed = new DependentStepsWorkflow();
        var resumedEngine = CreateEngine(resumed, "deps");
        await resumedEngine.ExecuteAsync(
            runId,
            TestContext.Current.CancellationToken);
        var rerun = await resumedEngine.WaitForCompletionAsync<string>(
            runId,
            cancellationToken: TestContext.Current.CancellationToken);

        rerun.Should().Be("D[B[A(x)]]|E[C(x)]");
        resumed.ACalls.Should().Be(0);
        resumed.CCalls.Should().Be(0);
        resumed.ECalls.Should().Be(0);
        resumed.BCalls.Should().Be(1);
        resumed.DCalls.Should().Be(1);
        (await resumedEngine.GetStepsAsync(
            runId,
            TestContext.Current.CancellationToken)).Should().Contain(item =>
            item.StepKey == "b" && item.Revision == 2);
    }

    [Fact]
    public async Task RestartStepAsync_ConcurrentAndRepeatedRestartsSettleConsistently()
    {
        var workflow = new DependentStepsWorkflow();
        var engine = CreateEngine(workflow, "deps-repeat");
        await engine.RunAsync<string, string>(
            "deps-repeat",
            "1",
            "x",
            cancellationToken: TestContext.Current.CancellationToken);
        var runId = workflow.RunId;

        var first = engine.RestartStepAsync(
            runId,
            "b",
            cancellationToken: TestContext.Current.CancellationToken);
        var second = engine.RestartStepAsync(
            runId,
            "b",
            cancellationToken: TestContext.Current.CancellationToken);
        await Task.WhenAll(first, second);
        (await first).StepsToInvalidate.Select(item => item.StepKey)
            .Should().Equal("b", "d");
        (await second).StepsToInvalidate.Select(item => item.StepKey)
            .Should().Equal("b", "d");

        await engine.RestartStepAsync(
            runId,
            "b",
            cancellationToken: TestContext.Current.CancellationToken);

        var resumed = new DependentStepsWorkflow();
        var resumedEngine = CreateEngine(resumed, "deps-repeat");
        await resumedEngine.ExecuteAsync(
            runId,
            TestContext.Current.CancellationToken);
        var rerun = await resumedEngine.WaitForCompletionAsync<string>(
            runId,
            cancellationToken: TestContext.Current.CancellationToken);

        rerun.Should().Be("D[B[A(x)]]|E[C(x)]");
        resumed.BCalls.Should().Be(1);
        resumed.DCalls.Should().Be(1);
    }

    [Fact]
    public async Task StepAsync_WithCompensation_PersistsPendingCompensationWithCommittedResult()
    {
        var workflow = new CompensationWorkflow();
        var engine = CreateEngine(workflow, "compensate");
        var result = await engine.RunAsync<string, string>(
            "compensate",
            "1",
            "x",
            cancellationToken: TestContext.Current.CancellationToken);

        result.Should().Be("confirmed:vm-x");
        workflow.ReserveCalls.Should().Be(1);
        workflow.CompensationCalls.Should().Be(0);

        var compensations = await engine.GetCompensationsAsync(
            workflow.RunId,
            TestContext.Current.CancellationToken);
        compensations.Should().ContainSingle();
        var compensation = compensations.Single();
        compensation.StepKey.Should().Be("reserve");
        compensation.Revision.Should().Be(1);
        compensation.Status.Should().Be(CompensationStatus.Pending);
        compensation.CompensationName.Should().Be("reserve");
        compensation.InputJson.Should().Be("\"vm-x\"");
        compensation.InputType.Should().NotBeNullOrEmpty();
        compensation.IdempotencyKey.Should().Be(
            $"{workflow.RunId:D}:reserve:1:compensation");
        compensation.RetryPolicyJson.Should().NotBeNullOrEmpty();
        compensation.LeaseGeneration.Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task StepAsync_WithoutCompensation_RecordsNoCompensations()
    {
        var workflow = new TwoStepWorkflow();
        var engine = CreateEngine(workflow, "no-compensate");
        await engine.RunAsync<string, string>(
            "no-compensate",
            "1",
            "value",
            cancellationToken: TestContext.Current.CancellationToken);

        (await engine.GetCompensationsAsync(
            workflow.RunId,
            TestContext.Current.CancellationToken)).Should().BeEmpty();
    }

    [Fact]
    public async Task StepAsync_CompensatedStepTerminalFailure_MarksCompensationSkipped()
    {
        var workflow = new FailingCompensatedWorkflow();
        var engine = CreateEngine(workflow, "compensate-fail");
        var runId = await engine.StartAsync(
            "compensate-fail",
            "1",
            "x",
            cancellationToken: TestContext.Current.CancellationToken);
        await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);

        (await engine.GetRunAsync(
            runId,
            TestContext.Current.CancellationToken))!.Status
            .Should().Be(WorkflowStatus.Failed);
        var compensations = await engine.GetCompensationsAsync(
            runId,
            TestContext.Current.CancellationToken);
        compensations.Should().ContainSingle()
            .Which.Status.Should().Be(CompensationStatus.Skipped);
    }

    [Fact]
    public async Task StepAsync_CompensatedStepRestart_CreatesCompensationForNewRevision()
    {
        var workflow = new CompensationWorkflow();
        var engine = CreateEngine(workflow, "compensate-restart");
        await engine.RunAsync<string, string>(
            "compensate-restart",
            "1",
            "x",
            cancellationToken: TestContext.Current.CancellationToken);
        var runId = workflow.RunId;

        await engine.RestartStepAsync(
            runId,
            "reserve",
            cancellationToken: TestContext.Current.CancellationToken);

        var resumed = new CompensationWorkflow();
        var resumedEngine = CreateEngine(resumed, "compensate-restart");
        await resumedEngine.ExecuteAsync(
            runId,
            TestContext.Current.CancellationToken);
        var rerun = await resumedEngine.WaitForCompletionAsync<string>(
            runId,
            cancellationToken: TestContext.Current.CancellationToken);

        rerun.Should().Be("confirmed:vm-x");
        (await resumedEngine.GetStepsAsync(
            runId,
            TestContext.Current.CancellationToken)).Should().Contain(item =>
            item.StepKey == "reserve" && item.Revision == 2);

        var compensations = await resumedEngine.GetCompensationsAsync(
            runId,
            TestContext.Current.CancellationToken);
        compensations.Should().HaveCount(2);
        compensations.Select(item => item.Revision).Should().Equal(1, 2);
        compensations.Should().OnlyContain(item =>
            item.Status == CompensationStatus.Pending &&
            item.InputJson == "\"vm-x\"");
    }

    [Fact]
    public async Task PlanRollbackAsync_FullRollback_ListsClaimableStepsInReverseDependencyOrder()
    {
        var workflow = new RollbackWorkflow();
        var engine = CreateEngine(workflow, "rollback");
        await engine.RunAsync<string, string>(
            "rollback",
            "1",
            "x",
            cancellationToken: TestContext.Current.CancellationToken);

        var plan = await engine.PlanRollbackAsync(
            workflow.RunId,
            TestContext.Current.CancellationToken);

        plan.TargetStepKey.Should().BeNull();
        plan.Steps.Where(item => item.Action == RollbackAction.Compensate)
            .Select(item => item.StepKey)
            .Should().Equal("tests", "deploy", "frontend", "payment");
        plan.Steps.Where(item => item.Action == RollbackAction.Compensate)
            .Should().OnlyContain(item =>
                item.Reason == RollbackReason.Dependent);
        plan.Steps.Where(item => item.Action == RollbackAction.Preserve)
            .Select(item => item.StepKey)
            .Should().Equal("plan");
    }

    [Fact]
    public async Task PlanRollbackAsync_BeforeStep_IncludesTargetAsBoundary()
    {
        var workflow = new RollbackWorkflow();
        var engine = CreateEngine(workflow, "rollback");
        await engine.RunAsync<string, string>(
            "rollback",
            "1",
            "x",
            cancellationToken: TestContext.Current.CancellationToken);

        var plan = await engine.PlanRollbackAsync(
            workflow.RunId,
            "deploy",
            new RollbackOptions(RollbackBoundary.BeforeStep),
            TestContext.Current.CancellationToken);

        plan.TargetStepKey.Should().Be("deploy");
        plan.Steps.Should().Equal(
            new RollbackPlanStep("tests", RollbackAction.Compensate, RollbackReason.Dependent),
            new RollbackPlanStep("deploy", RollbackAction.Compensate, RollbackReason.Boundary),
            new RollbackPlanStep("plan", RollbackAction.Preserve, RollbackReason.Ancestor),
            new RollbackPlanStep("payment", RollbackAction.Preserve, RollbackReason.Ancestor),
            new RollbackPlanStep("frontend", RollbackAction.Preserve, RollbackReason.IndependentBranch));
    }

    [Fact]
    public async Task PlanRollbackAsync_AfterStep_PreservesTarget()
    {
        var workflow = new RollbackWorkflow();
        var engine = CreateEngine(workflow, "rollback");
        await engine.RunAsync<string, string>(
            "rollback",
            "1",
            "x",
            cancellationToken: TestContext.Current.CancellationToken);

        var plan = await engine.PlanRollbackAsync(
            workflow.RunId,
            "deploy",
            new RollbackOptions(RollbackBoundary.AfterStep),
            TestContext.Current.CancellationToken);

        plan.Steps.Where(item => item.Action == RollbackAction.Compensate)
            .Select(item => item.StepKey)
            .Should().Equal("tests");
        plan.Steps.Should().Contain(item =>
            item.StepKey == "deploy" &&
            item.Action == RollbackAction.Preserve &&
            item.Reason == RollbackReason.Boundary);
    }

    [Fact]
    public async Task RollbackAsync_CompensatesCompletedStepsInReverseDependencyOrder()
    {
        var forward = new RollbackWorkflow();
        var engine = CreateEngine(forward, "rollback");
        await engine.RunAsync<string, string>(
            "rollback",
            "1",
            "x",
            cancellationToken: TestContext.Current.CancellationToken);
        var runId = forward.RunId;

        var rollbackWorkflow = new RollbackWorkflow();
        var rollbackEngine = CreateEngine(rollbackWorkflow, "rollback");
        await rollbackEngine.RollbackAsync(runId, cancellationToken: TestContext.Current.CancellationToken);

        rollbackWorkflow.CompensatedOrder
            .Should().Equal("tests", "deploy", "frontend", "payment");
        forward.CompensatedOrder.Should().BeEmpty();
        (await rollbackEngine.GetRunAsync(
            runId,
            TestContext.Current.CancellationToken))!.Status
            .Should().Be(WorkflowStatus.Compensated);
        (await rollbackEngine.GetCompensationsAsync(
            runId,
            TestContext.Current.CancellationToken))
            .Should().OnlyContain(item =>
                item.Status == CompensationStatus.Completed);
        (await rollbackEngine.GetEventsAsync(
            runId,
            cancellationToken: TestContext.Current.CancellationToken))
            .Should().Contain(item =>
                item.EventType == WorkflowEventTypes.WorkflowCompensated);
    }

    [Fact]
    public async Task RollbackAsync_SecondAttemptReusesCompletedCompensations()
    {
        var forward = new RollbackWorkflow();
        var engine = CreateEngine(forward, "rollback");
        await engine.RunAsync<string, string>(
            "rollback",
            "1",
            "x",
            cancellationToken: TestContext.Current.CancellationToken);
        var runId = forward.RunId;

        var first = new RollbackWorkflow();
        await CreateEngine(first, "rollback").RollbackAsync(runId, cancellationToken: TestContext.Current.CancellationToken);
        first.CompensatedOrder.Should().HaveCount(4);

        var second = new RollbackWorkflow();
        await CreateEngine(second, "rollback").RollbackAsync(runId, cancellationToken: TestContext.Current.CancellationToken);

        first.CompensatedOrder.Should().HaveCount(4);
        second.CompensatedOrder.Should().BeEmpty();
        (await engine.GetRunAsync(
            runId,
            TestContext.Current.CancellationToken))!.Status
            .Should().Be(WorkflowStatus.Compensated);
    }

    [Fact]
    public async Task RollbackAsync_FailedCompensation_FailsRunAndReusesCompletedOnRetry()
    {
        var forward = new RollbackWorkflow();
        var engine = CreateEngine(forward, "rollback");
        await engine.RunAsync<string, string>(
            "rollback",
            "1",
            "x",
            cancellationToken: TestContext.Current.CancellationToken);
        var runId = forward.RunId;

        var failing = new RollbackWorkflow(failCompensations: "payment");
        var failingEngine = CreateEngine(failing, "rollback");
        var act = async () => await failingEngine.RollbackAsync(runId, cancellationToken: TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<RollbackFailedException>();
        failing.CompensatedOrder.Should().Equal(
            "tests", "deploy", "frontend", "payment");
        (await failingEngine.GetRunAsync(
            runId,
            TestContext.Current.CancellationToken))!.Status
            .Should().Be(WorkflowStatus.Failed);

        var retry = new RollbackWorkflow();
        var retryEngine = CreateEngine(retry, "rollback");
        await retryEngine.RollbackAsync(runId, cancellationToken: TestContext.Current.CancellationToken);

        retry.CompensatedOrder.Should().Equal("payment");
        failing.CompensatedOrder.Should().Equal(
            "tests", "deploy", "frontend", "payment");
        (await retryEngine.GetRunAsync(
            runId,
            TestContext.Current.CancellationToken))!.Status
            .Should().Be(WorkflowStatus.Compensated);
        (await retryEngine.GetCompensationsAsync(
            runId,
            TestContext.Current.CancellationToken))
            .Should().OnlyContain(item => item.Status == CompensationStatus.Completed);
    }

    [Fact]
    public async Task RollbackToStepAsync_AfterStep_CompensatesOnlyDependents()
    {
        var forward = new RollbackWorkflow();
        var engine = CreateEngine(forward, "rollback");
        await engine.RunAsync<string, string>(
            "rollback",
            "1",
            "x",
            cancellationToken: TestContext.Current.CancellationToken);
        var runId = forward.RunId;

        var rollbackWorkflow = new RollbackWorkflow();
        var rollbackEngine = CreateEngine(rollbackWorkflow, "rollback");
        await rollbackEngine.RollbackToStepAsync(
            runId,
            "deploy",
            RollbackBoundary.AfterStep,
            cancellationToken: TestContext.Current.CancellationToken);

        rollbackWorkflow.CompensatedOrder.Should().Equal("tests");
        (await rollbackEngine.GetCompensationsAsync(
            runId,
            TestContext.Current.CancellationToken))
            .Should().Contain(item =>
                item.StepKey == "tests" && item.Status == CompensationStatus.Completed)
            .And.Contain(item =>
                item.StepKey == "deploy" && item.Status == CompensationStatus.Pending)
            .And.Contain(item =>
                item.StepKey == "payment" && item.Status == CompensationStatus.Pending)
            .And.Contain(item =>
                item.StepKey == "frontend" && item.Status == CompensationStatus.Pending);
        (await rollbackEngine.GetRunAsync(
            runId,
            TestContext.Current.CancellationToken))!.Status
            .Should().Be(WorkflowStatus.Compensated);
    }

    [Fact]
    public async Task RollbackToStepAsync_BeforeStep_CompensatesTargetToo()
    {
        var forward = new RollbackWorkflow();
        var engine = CreateEngine(forward, "rollback");
        await engine.RunAsync<string, string>(
            "rollback",
            "1",
            "x",
            cancellationToken: TestContext.Current.CancellationToken);
        var runId = forward.RunId;

        var rollbackWorkflow = new RollbackWorkflow();
        var rollbackEngine = CreateEngine(rollbackWorkflow, "rollback");
        await rollbackEngine.RollbackToStepAsync(
            runId,
            "deploy",
            RollbackBoundary.BeforeStep,
            cancellationToken: TestContext.Current.CancellationToken);

        rollbackWorkflow.CompensatedOrder
            .Should().Equal("tests", "deploy");
        (await rollbackEngine.GetCompensationsAsync(
            runId,
            TestContext.Current.CancellationToken))
            .Should().Contain(item =>
                item.StepKey == "deploy" && item.Status == CompensationStatus.Completed)
            .And.Contain(item =>
                item.StepKey == "payment" && item.Status == CompensationStatus.Pending);
    }

    [Fact]
    public async Task RollbackAsync_StepWithCommittedResult_SendsItToTheCompensation()
    {
        var forward = new CapturingCompensationWorkflow();
        var engine = CreateEngine(forward, "rollback-result");
        await engine.RunAsync<string, string>(
            "rollback-result",
            "1",
            "x",
            cancellationToken: TestContext.Current.CancellationToken);
        var runId = forward.RunId;

        var rollbackWorkflow = new CapturingCompensationWorkflow();
        var rollbackEngine = CreateEngine(rollbackWorkflow, "rollback-result");
        await rollbackEngine.RollbackAsync(runId, cancellationToken: TestContext.Current.CancellationToken);

        rollbackWorkflow.CompensatedResults.Single().Should().Be("vm-x");
        rollbackWorkflow.CompensationKeys.Single().Should().Be(
            $"{runId:D}:reserve:1:compensation");
    }

    [Fact]
    public async Task RollbackAndRestartAsync_CompensatesThenRestartsForward()
    {
        var forward = new RollbackWorkflow();
        var engine = CreateEngine(forward, "rollback");
        await engine.RunAsync<string, string>(
            "rollback",
            "1",
            "x",
            cancellationToken: TestContext.Current.CancellationToken);
        var runId = forward.RunId;

        var rollbackWorkflow = new RollbackWorkflow();
        var rollbackEngine = CreateEngine(rollbackWorkflow, "rollback");
        await rollbackEngine.RollbackAndRestartAsync(
            runId,
            cancellationToken: TestContext.Current.CancellationToken);

        rollbackWorkflow.CompensatedOrder.Should().Equal(
            "tests", "deploy", "frontend", "payment");
        (await rollbackEngine.GetRunAsync(
            runId,
            TestContext.Current.CancellationToken))!.Status
            .Should().Be(WorkflowStatus.Pending);
        (await rollbackEngine.GetStepsAsync(
            runId,
            TestContext.Current.CancellationToken))
            .Should().OnlyContain(item => item.Status == StepStatus.Pending)
            .And.OnlyContain(item => item.Revision == 2);
        (await rollbackEngine.GetCompensationsAsync(
            runId,
            TestContext.Current.CancellationToken))
            .Should().OnlyContain(item => item.Status == CompensationStatus.Completed);
        (await rollbackEngine.GetEventsAsync(
            runId,
            cancellationToken: TestContext.Current.CancellationToken))
            .Should().Contain(item =>
                item.EventType == WorkflowEventTypes.WorkflowRestarted);

        var rerunWorkflow = new RollbackWorkflow();
        var rerunEngine = CreateEngine(rerunWorkflow, "rollback");
        var result = await rerunEngine.RunAsync<string, string>(
            "rollback",
            "1",
            "x",
            workflowRunId: runId,
            cancellationToken: TestContext.Current.CancellationToken);
        result.Should().Be("done");
        (await rerunEngine.GetRunAsync(
            runId,
            TestContext.Current.CancellationToken))!.Status
            .Should().Be(WorkflowStatus.Completed);
    }

    [Fact]
    public async Task RollbackAndRestartAsync_ResumesInterruptedOperationOnExecute()
    {
        var forward = new RollbackWorkflow();
        var engine = CreateEngine(forward, "rollback");
        await engine.RunAsync<string, string>(
            "rollback",
            "1",
            "x",
            cancellationToken: TestContext.Current.CancellationToken);
        var runId = forward.RunId;

        var store = CreateStore();
        var now = DateTimeOffset.UtcNow;
        await store.CreateOperationAsync(
            new WorkflowRunOperation
            {
                OperationId = Guid.NewGuid(),
                WorkflowRunId = runId,
                OperationType = "rollback-and-restart",
                Status = WorkflowOperationStatus.Requested,
                PayloadJson = null,
                CreatedAt = now,
                UpdatedAt = now
            },
            TestContext.Current.CancellationToken);
        await store.ClaimRollbackAndRestartAsync(
            runId,
            "crashed-worker",
            now,
            now.AddSeconds(-1),
            TestContext.Current.CancellationToken);

        var resumedWorkflow = new RollbackWorkflow();
        var resumedEngine = CreateEngine(resumedWorkflow, "rollback");
        await resumedEngine.ExecuteAsync(
            runId,
            TestContext.Current.CancellationToken);

        resumedWorkflow.CompensatedOrder.Should().Equal(
            "tests", "deploy", "frontend", "payment");
        (await resumedEngine.GetRunAsync(
            runId,
            TestContext.Current.CancellationToken))!.Status
            .Should().Be(WorkflowStatus.Pending);
        (await resumedEngine.GetEventsAsync(
            runId,
            cancellationToken: TestContext.Current.CancellationToken))
            .Should().Contain(item =>
                item.EventType == WorkflowEventTypes.WorkflowRestarted);

        await resumedEngine.ExecuteAsync(
            runId,
            TestContext.Current.CancellationToken);
        (await resumedEngine.GetRunAsync(
            runId,
            TestContext.Current.CancellationToken))!.Status
            .Should().Be(WorkflowStatus.Completed);
    }

    [Fact]
    public async Task RollbackAndRestartAsync_RunAvailablePicksUpCrashedOperation()
    {
        var forward = new RollbackWorkflow();
        var engine = CreateEngine(forward, "rollback");
        await engine.RunAsync<string, string>(
            "rollback",
            "1",
            "x",
            cancellationToken: TestContext.Current.CancellationToken);
        var runId = forward.RunId;

        var store = CreateStore();
        var now = DateTimeOffset.UtcNow;
        await store.CreateOperationAsync(
            new WorkflowRunOperation
            {
                OperationId = Guid.NewGuid(),
                WorkflowRunId = runId,
                OperationType = "rollback-and-restart",
                Status = WorkflowOperationStatus.Requested,
                PayloadJson = null,
                CreatedAt = now,
                UpdatedAt = now
            },
            TestContext.Current.CancellationToken);
        await store.ClaimRollbackAndRestartAsync(
            runId,
            "crashed-worker",
            now,
            now.AddSeconds(-1),
            TestContext.Current.CancellationToken);

        var recoveredWorkflow = new RollbackWorkflow();
        var recoveredEngine = CreateEngine(recoveredWorkflow, "rollback");
        var picked = await recoveredEngine.RunAvailableAsync(
            TestContext.Current.CancellationToken);

        picked.Should().BeGreaterThan(0);
        recoveredWorkflow.CompensatedOrder.Should().Equal(
            "tests", "deploy", "frontend", "payment");
        (await recoveredEngine.GetRunAsync(
            runId,
            TestContext.Current.CancellationToken))!.Status
            .Should().Be(WorkflowStatus.Pending);
    }

    [Fact]
    public async Task RestartStepAsync_StepOnlyMode_InvalidatesJustTheTarget()
    {
        var workflow = new DependentStepsWorkflow();
        var engine = CreateEngine(workflow, "deps-step-only");
        await engine.RunAsync<string, string>(
            "deps-step-only",
            "1",
            "x",
            cancellationToken: TestContext.Current.CancellationToken);

        var plan = await engine.RestartStepAsync(
            workflow.RunId,
            "b",
            new RestartStepOptions { Mode = StepRestartMode.StepOnly },
            cancellationToken: TestContext.Current.CancellationToken);

        plan.StepsToInvalidate.Select(item => item.StepKey)
            .Should().Equal("b");
    }

    [Fact]
    public async Task RestartStepAsync_CreationOrderMode_FallsBackToLegacyBehavior()
    {
        var workflow = new DependentStepsWorkflow();
        var engine = CreateEngine(workflow, "deps-creation-order");
        await engine.RunAsync<string, string>(
            "deps-creation-order",
            "1",
            "x",
            cancellationToken: TestContext.Current.CancellationToken);

        var plan = await engine.RestartStepAsync(
            workflow.RunId,
            "b",
            new RestartStepOptions { Mode = StepRestartMode.CreationOrder },
            cancellationToken: TestContext.Current.CancellationToken);

        plan.StepsToInvalidate.Select(item => item.StepKey)
            .Should().Equal("b", "d", "e");
        plan.StepsToInvalidate.Should().Contain(item =>
            item.StepKey == "d" && item.Reason == RestartReason.CreationOrderFallback);
    }

    [Fact]
    public async Task RestartStepAsync_FanOut_ReusesSiblingItems()
    {
        var workflow = new FanOutWorkflow();
        var engine = CreateEngine(workflow, "fanout-restart");
        var result = await engine.RunAsync<string, string>(
            "fanout-restart",
            "1",
            "a,b,c",
            cancellationToken: TestContext.Current.CancellationToken);

        result.Should().Be("A:B:C");
        workflow.Calls.Should().Be(3);
        var runId = workflow.RunId;

        var plan = await engine.RestartStepAsync(
            runId,
            "process.1",
            cancellationToken: TestContext.Current.CancellationToken);
        plan.StepsToInvalidate.Select(item => item.StepKey)
            .Should().Equal("process.1");

        var resumed = new FanOutWorkflow();
        var resumedEngine = CreateEngine(resumed, "fanout-restart");
        await resumedEngine.ExecuteAsync(
            runId,
            TestContext.Current.CancellationToken);
        var rerun = await resumedEngine.WaitForCompletionAsync<string>(
            runId,
            cancellationToken: TestContext.Current.CancellationToken);

        rerun.Should().Be("A:B:C");
        resumed.Calls.Should().Be(1);
    }

    [Fact]
    public async Task StartChildAsync_RecordsWaitToStartDependencyEdge()
    {
        var parent = new ParentWorkflow();
        var child = new ChildWorkflow();
        var engine = CreateEngine(
            new WorkflowRegistry()
                .Register("parent-edge", "1", parent)
                .Register("child", "1", child));
        var runId = await engine.StartAsync(
            "parent-edge",
            "1",
            "go",
            cancellationToken: TestContext.Current.CancellationToken);
        await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
        await engine.WaitForCompletionAsync<string>(
            runId,
            cancellationToken: TestContext.Current.CancellationToken);

        var graph = await engine.GetDependencyGraphAsync(
            runId,
            TestContext.Current.CancellationToken);
        graph.Should().Contain(item =>
            item.StepKey == "child:wait" && item.DependsOnStepKey == "child:start");

        var plan = await engine.RestartStepAsync(
            runId,
            "child:start",
            cancellationToken: TestContext.Current.CancellationToken);
        plan.StepsToInvalidate.Select(item => item.StepKey)
            .Should().Equal("child:start", "child:wait");

        await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
        var rerun = await engine.WaitForCompletionAsync<string>(
            runId,
            cancellationToken: TestContext.Current.CancellationToken);
        rerun.Should().Be("child:parent:go");
        (await engine.GetRunsAsync(
            new RunQuery { WorkflowName = "child" },
            cancellationToken: TestContext.Current.CancellationToken))
            .Should().ContainSingle();
    }

    [Fact]
    public async Task Store_RestartStepAsync_FencesOutStaleWorkerWrites()
    {
        var store = CreateStore();
        await store.InitializeAsync(TestContext.Current.CancellationToken);
        var now = DateTimeOffset.UtcNow;
        var runId = Guid.NewGuid();
        await store.CreateRunAsync(
            new WorkflowRun
            {
                Id = runId,
                WorkflowName = "fence",
                WorkflowVersion = "1",
                Status = WorkflowStatus.Pending,
                CreatedAt = now,
                UpdatedAt = now
            },
            TestContext.Current.CancellationToken);
        var generation = await store.TryClaimRunAsync(
            runId,
            "worker",
            now,
            now.AddMinutes(1),
            TestContext.Current.CancellationToken);
        generation.Should().NotBeNull();
        var claim = await store.ClaimStepAsync(
            new StepClaimRequest
            {
                WorkflowRunId = runId,
                StepKey = "first",
                InputJson = "1",
                InputType = "int",
                InputHash = "one",
                OutputType = "string",
                OwnerId = "worker",
                Now = now,
                LeaseExpiresAt = now.AddMinutes(1),
                LeaseGeneration = generation!.Value
            },
            TestContext.Current.CancellationToken);
        claim.Disposition.Should().Be(StepClaimDisposition.Acquired);
        var staleStepId = claim.Step.Id;

        var plan = await store.RestartStepAsync(
            runId,
            "first",
            StepRestartMode.Dependents,
            null,
            null,
            now.AddSeconds(1),
            TestContext.Current.CancellationToken);
        plan.StepsToInvalidate.Should().ContainSingle();

        (await store.RenewStepLeaseAsync(
            staleStepId,
            "worker",
            now.AddMinutes(2),
            TestContext.Current.CancellationToken)).Should().BeFalse();
        var complete = () => store.CompleteStepAsync(
            staleStepId,
            "worker",
            "\"done\"",
            now.AddSeconds(2),
            TestContext.Current.CancellationToken).AsTask();
        await complete.Should().ThrowAsync<WorkflowStateException>();
        var staleClaim = () => store.ClaimStepAsync(
            new StepClaimRequest
            {
                WorkflowRunId = runId,
                StepKey = "other",
                InputJson = "1",
                InputType = "int",
                InputHash = "one",
                OutputType = "string",
                OwnerId = "worker",
                Now = now.AddSeconds(2),
                LeaseExpiresAt = now.AddMinutes(1),
                LeaseGeneration = generation.Value
            },
            TestContext.Current.CancellationToken).AsTask();
        await staleClaim.Should().ThrowAsync<LeaseLostException>();

        var fresh = await store.TryClaimRunAsync(
            runId,
            "fresh",
            now.AddSeconds(3),
            now.AddMinutes(2),
            TestContext.Current.CancellationToken);
        fresh.Should().NotBeNull();
        var freshClaim = await store.ClaimStepAsync(
            new StepClaimRequest
            {
                WorkflowRunId = runId,
                StepKey = "first",
                InputJson = "1",
                InputType = "int",
                InputHash = "one",
                OutputType = "string",
                OwnerId = "fresh",
                Now = now.AddSeconds(3),
                LeaseExpiresAt = now.AddMinutes(2),
                LeaseGeneration = fresh!.Value
            },
            TestContext.Current.CancellationToken);
        freshClaim.Disposition.Should().Be(StepClaimDisposition.Acquired);
        freshClaim.Step.Revision.Should().Be(2);
    }

    [Fact]
    public async Task GetRunProgressAsync_ReturnsSnapshotOfParentAndChildRuns()
    {
        var parent = new ParentWorkflow();
        var child = new ChildWorkflow();
        var engine = CreateEngine(
            new WorkflowRegistry()
                .Register("parent", "1", parent)
                .Register("child", "1", child));
        var runId = await engine.StartAsync(
            "parent",
            "1",
            "go",
            cancellationToken: TestContext.Current.CancellationToken);
        await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
        await engine.WaitForCompletionAsync<string>(
            runId,
            cancellationToken: TestContext.Current.CancellationToken);

        var progress = await engine.GetRunProgressAsync(
            runId,
            cancellationToken: TestContext.Current.CancellationToken);

        progress.Should().NotBeNull();
        progress!.Run.Id.Should().Be(runId);
        progress.Run.Status.Should().Be(WorkflowStatus.Completed);
        progress.Steps.Should().HaveCount(3);
        progress.CompletedSteps.Should().Be(3);
        progress.RunningSteps.Should().Be(0);
        progress.WaitingSteps.Should().Be(0);
        progress.FailedSteps.Should().Be(0);
        progress.ExecutedStepKeys.Should().Equal(
            "parent-step", "child:start", "child:wait");
        progress.Events.Should().NotBeEmpty();
        progress.Children.Should().ContainSingle();
        var childProgress = progress.Children[0];
        childProgress.Run.ParentRunId.Should().Be(runId);
        childProgress.Run.Status.Should().Be(WorkflowStatus.Completed);
        childProgress.Steps.Should().ContainSingle()
            .Which.StepKey.Should().Be("child-step");
        childProgress.CompletedSteps.Should().Be(1);
        childProgress.ExecutedStepKeys.Should().Equal("child-step");
        childProgress.Events.Should().NotBeEmpty();
        childProgress.Children.Should().BeEmpty();
    }

    [Fact]
    public async Task GetRunProgressAsync_UnknownRun_ReturnsNull()
    {
        var engine = CreateEngine(new TwoStepWorkflow(), "progress-unknown");
        var progress = await engine.GetRunProgressAsync(
            Guid.NewGuid(),
            cancellationToken: TestContext.Current.CancellationToken);
        progress.Should().BeNull();
    }

    [Fact]
    public async Task GetRunProgressAsync_IncludeEventsFalse_OmitsEvents()
    {
        var engine = CreateEngine(new TwoStepWorkflow(), "progress-no-events");
        var runId = await engine.StartAsync(
            "progress-no-events",
            "1",
            "value",
            cancellationToken: TestContext.Current.CancellationToken);
        await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
        await engine.WaitForCompletionAsync<string>(
            runId,
            cancellationToken: TestContext.Current.CancellationToken);

        var progress = await engine.GetRunProgressAsync(
            runId,
            new RunProgressOptions { IncludeEvents = false },
            cancellationToken: TestContext.Current.CancellationToken);

        progress!.Events.Should().BeEmpty();
        progress.Steps.Should().HaveCount(2);
        progress.CompletedSteps.Should().Be(2);
    }

    [Fact]
    public async Task WaitForCompletionAsync_AfterDeadline_ThrowsTimeout()
    {
        var workflow = new SlowWorkflow();
        var engine = CreateEngine(workflow, "wait-timeout");
        var runId = await engine.StartAsync(
            "wait-timeout",
            "1",
            "value",
            cancellationToken: TestContext.Current.CancellationToken);

        var action = () => engine.WaitForCompletionAsync<string>(
            runId,
            DateTimeOffset.UtcNow.AddMilliseconds(20),
            TestContext.Current.CancellationToken);

        await action.Should().ThrowAsync<TimeoutException>()
            .WithMessage("*wait deadline*");
    }

    [Fact]
    public async Task FanOutAsync_ExecutesEachItemAsADurableStep()
    {
        var workflow = new FanOutWorkflow();
        var engine = CreateEngine(workflow, "fanout");

        var result = await engine.RunAsync<string, string>(
            "fanout",
            "1",
            "a,b,c",
            cancellationToken: TestContext.Current.CancellationToken);

        result.Should().Be("A:B:C");
        workflow.Calls.Should().Be(3);
        var steps = await engine.GetStepsAsync(
            workflow.RunId,
            TestContext.Current.CancellationToken);
        steps.Should().HaveCount(3)
            .And.OnlyContain(step => step.Status == StepStatus.Completed);
    }

    [Fact]
    public async Task EmitAsync_ForwardsCommittedEventToPublisher()
    {
        var workflow = new ProgressWorkflow();
        var publisher = new RecordingPublisher();
        var registry = new WorkflowRegistry()
            .Register("progress-pub", "1", workflow);
        var engine = new WorkflowEngine(
            CreateStore(),
            registry,
            new ZhinuOptions
            {
                PollInterval = TimeSpan.FromMilliseconds(10)
            },
            eventPublisher: publisher);

        var result = await engine.RunAsync<string, string>(
            "progress-pub",
            "1",
            "value",
            cancellationToken: TestContext.Current.CancellationToken);

        result.Should().Be("value-done");
        publisher.Events.Select(@event => @event.EventType)
            .Should().Contain(WorkflowEventTypes.Progress);
        publisher.Events.Should().HaveCount(3);
    }

    private sealed class RecordingPublisher : IWorkflowEventPublisher
    {
        public List<WorkflowEvent> Events { get; } = new();

        public Task PublishAsync(
            WorkflowEvent @event,
            CancellationToken cancellationToken = default)
        {
            Events.Add(@event);
            return Task.CompletedTask;
        }
    }

    private WorkflowEngine CreateEngine<TWorkflow>(
        TWorkflow workflow,
        string name,
        TimeSpan? leaseDuration = null)
        where TWorkflow : class, IWorkflow<string, string> =>
        CreateEngine(
            new WorkflowRegistry().Register(name, "1", workflow),
            leaseDuration);

    private WorkflowEngine CreateEngine(
        WorkflowRegistry registry,
        TimeSpan? leaseDuration = null)
    {
        var duration = leaseDuration ?? TimeSpan.FromSeconds(2);
        return new WorkflowEngine(
            CreateStore(),
            registry,
            new ZhinuOptions
            {
                LeaseDuration = duration,
                LeaseRenewalInterval = TimeSpan.FromTicks(duration.Ticks / 3),
                PollInterval = TimeSpan.FromMilliseconds(10)
            });
    }

    private static async Task WaitUntilAsync(
        Func<Task<bool>> condition,
        CancellationToken cancellationToken)
    {
        while (!await condition().ConfigureAwait(false))
        {
            await Task.Delay(25, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<bool> HasStepStatusAsync(
        WorkflowEngine engine,
        Guid runId,
        string stepKey,
        StepStatus status,
        CancellationToken cancellationToken)
    {
        var steps = await engine.GetStepsAsync(runId, cancellationToken);
        return steps.Any(item => item.StepKey == stepKey && item.Status == status);
    }

    private SqliteWorkflowStore CreateStore() =>
        new(new ZhinuSqliteOptions
        {
            DatabasePath = Path.Combine(root, "zhinu.db"),
            BusyTimeout = TimeSpan.FromSeconds(2)
        });

    private static async Task<Guid> CreateRunAsync(
        SqliteWorkflowStore store,
        string workflowName,
        DateTimeOffset createdAt,
        Guid? parentRunId,
        CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        await store.CreateRunAsync(
            new WorkflowRun
            {
                Id = id,
                WorkflowName = workflowName,
                WorkflowVersion = "1",
                Status = WorkflowStatus.Pending,
                CreatedAt = createdAt,
                UpdatedAt = createdAt,
                ParentRunId = parentRunId
            },
            cancellationToken);
        return id;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    private sealed class TwoStepWorkflow : IWorkflow<string, string>
    {
        public int FirstCalls { get; private set; }
        public int SecondCalls { get; private set; }
        public Guid RunId { get; private set; }

        public async Task<string> RunAsync(
            WorkflowContext context,
            string input,
            CancellationToken cancellationToken)
        {
            RunId = context.WorkflowRunId;
            var first = await context.StepAsync(
                "first",
                input,
                (value, _) =>
                {
                    FirstCalls++;
                    return Task.FromResult($"Hello, {value}");
                },
                cancellationToken: cancellationToken);
            return await context.StepAsync(
                "second",
                first,
                (value, _) =>
                {
                    SecondCalls++;
                    return Task.FromResult($"{value}!");
                },
                cancellationToken: cancellationToken);
        }
    }

    private sealed class RestartableWorkflow : IWorkflow<string, string>
    {
        public string SecondSuffix = "a";
        public int FirstCalls;
        public int SecondCalls;
        public Guid RunId;

        public async Task<string> RunAsync(
            WorkflowContext context,
            string input,
            CancellationToken cancellationToken)
        {
            RunId = context.WorkflowRunId;
            var first = await context.StepAsync(
                "first",
                input,
                (value, _) =>
                {
                    FirstCalls++;
                    return Task.FromResult($"hello-{value}");
                },
                cancellationToken: cancellationToken);
            return await context.StepAsync(
                "second",
                first,
                (value, _) =>
                {
                    SecondCalls++;
                    return Task.FromResult($"{value}-{SecondSuffix}");
                },
                cancellationToken: cancellationToken);
        }
    }

    private sealed class DependentStepsWorkflow : IWorkflow<string, string>
    {
        public int ACalls;
        public int BCalls;
        public int CCalls;
        public int DCalls;
        public int ECalls;
        public Guid RunId { get; private set; }

        public async Task<string> RunAsync(
            WorkflowContext context,
            string input,
            CancellationToken cancellationToken)
        {
            RunId = context.WorkflowRunId;
            var a = await context.StepAsync(
                "a",
                input,
                (value, _) =>
                {
                    ACalls++;
                    return Task.FromResult($"A({value})");
                },
                cancellationToken: cancellationToken);
            var c = await context.StepAsync(
                "c",
                input,
                (value, _) =>
                {
                    CCalls++;
                    return Task.FromResult($"C({value})");
                },
                cancellationToken: cancellationToken);
            string b;
            using (context.DependsOn("a"))
            {
                b = await context.StepAsync(
                    "b",
                    a,
                    (value, _) =>
                    {
                        BCalls++;
                        return Task.FromResult($"B[{value}]");
                    },
                    cancellationToken: cancellationToken);
            }
            string d;
            using (context.DependsOn("b"))
            {
                d = await context.StepAsync(
                    "d",
                    b,
                    (value, _) =>
                    {
                        DCalls++;
                        return Task.FromResult($"D[{value}]");
                    },
                    cancellationToken: cancellationToken);
            }
            string e;
            using (context.DependsOn("c"))
            {
                e = await context.StepAsync(
                    "e",
                    c,
                    (value, _) =>
                    {
                        ECalls++;
                        return Task.FromResult($"E[{value}]");
                    },
                    cancellationToken: cancellationToken);
            }
            return $"{d}|{e}";
        }
    }

    private sealed class CompensationWorkflow : IWorkflow<string, string>
    {
        public int ReserveCalls;
        public int CompensationCalls;
        public Guid RunId { get; private set; }

        public async Task<string> RunAsync(
            WorkflowContext context,
            string input,
            CancellationToken cancellationToken)
        {
            RunId = context.WorkflowRunId;
            var reservation = await context.StepAsync(
                "reserve",
                input,
                (value, _) =>
                {
                    ReserveCalls++;
                    return Task.FromResult($"vm-{value}");
                },
                compensation: async (result, step, ct) =>
                {
                    CompensationCalls++;
                    await Task.Delay(1, ct);
                },
                cancellationToken: cancellationToken);
            return await context.StepAsync(
                "confirm",
                reservation,
                (value, _) => Task.FromResult($"confirmed:{value}"),
                cancellationToken: cancellationToken);
        }
    }

    private sealed class FailingCompensatedWorkflow : IWorkflow<string, string>
    {
        public Task<string> RunAsync(
            WorkflowContext context,
            string input,
            CancellationToken cancellationToken) =>
            context.StepAsync<string, string>(
                "reserve",
                input,
                (value, ct) => Task.FromException<string>(
                    new InvalidOperationException("quota exceeded")),
                compensation: (result, step, ct) => Task.CompletedTask,
                cancellationToken: cancellationToken);
    }

    private sealed class RollbackWorkflow : IWorkflow<string, string>
    {
        private readonly IReadOnlySet<string> failCompensations;

        public RollbackWorkflow(params string[] failCompensations)
        {
            this.failCompensations = new HashSet<string>(failCompensations);
        }

        public List<string> CompensatedOrder { get; } = [];

        public Guid RunId { get; private set; }

        public async Task<string> RunAsync(
            WorkflowContext context,
            string input,
            CancellationToken cancellationToken)
        {
            RunId = context.WorkflowRunId;
            var plan = await context.StepAsync(
                "plan",
                input,
                (value, _) => Task.FromResult($"plan-{value}"),
                cancellationToken: cancellationToken);
            string payment;
            using (context.DependsOn("plan"))
            {
                payment = await context.StepAsync(
                    "payment",
                    plan,
                    (value, _) => Task.FromResult($"paid:{value}"),
                    compensation: (result, step, ct) =>
                        Compensate("payment", cancellationToken),
                    cancellationToken: cancellationToken);
                await context.StepAsync(
                    "frontend",
                    plan,
                    (value, _) => Task.FromResult($"ui:{value}"),
                    compensation: (result, step, ct) =>
                        Compensate("frontend", cancellationToken),
                    cancellationToken: cancellationToken);
            }
            string deploy;
            using (context.DependsOn("payment"))
            {
                deploy = await context.StepAsync(
                    "deploy",
                    payment,
                    (value, _) => Task.FromResult($"deployed:{value}"),
                    compensation: (result, step, ct) =>
                        Compensate("deploy", cancellationToken),
                    cancellationToken: cancellationToken);
            }
            using (context.DependsOn("deploy"))
            {
                await context.StepAsync(
                    "tests",
                    deploy,
                    (value, _) => Task.FromResult($"tested:{value}"),
                    compensation: (result, step, ct) =>
                        Compensate("tests", cancellationToken),
                    cancellationToken: cancellationToken);
            }
            return "done";
        }

        private Task Compensate(
            string stepKey,
            CancellationToken cancellationToken)
        {
            CompensatedOrder.Add(stepKey);
            if (failCompensations.Contains(stepKey))
            {
                throw new InvalidOperationException(
                    $"Cannot undo '{stepKey}'.");
            }
            return Task.CompletedTask;
        }
    }

    private sealed class CapturingCompensationWorkflow : IWorkflow<string, string>
    {
        public List<string> CompensatedResults { get; } = [];

        public List<string> CompensationKeys { get; } = [];

        public Guid RunId { get; private set; }

        public async Task<string> RunAsync(
            WorkflowContext context,
            string input,
            CancellationToken cancellationToken)
        {
            RunId = context.WorkflowRunId;
            var reservation = await context.StepAsync(
                "reserve",
                input,
                (value, _) => Task.FromResult($"vm-{value}"),
                compensation: (result, step, ct) =>
                {
                    CompensatedResults.Add(result);
                    CompensationKeys.Add(step.IdempotencyKey);
                    return Task.CompletedTask;
                },
                cancellationToken: cancellationToken);
            return await context.StepAsync(
                "confirm",
                reservation,
                (value, _) => Task.FromResult($"confirmed:{value}"),
                cancellationToken: cancellationToken);
        }
    }

    private sealed class SignalWorkflow : IWorkflow<string, string>
    {
        public TimeSpan? WaitTimeout { get; set; }
        public Guid RunId { get; private set; }

        public async Task<string> RunAsync(
            WorkflowContext context,
            string input,
            CancellationToken cancellationToken)
        {
            RunId = context.WorkflowRunId;
            await context.StepAsync(
                "greet",
                input,
                (value, _) => Task.FromResult($"hello-{value}"),
                cancellationToken: cancellationToken);
            return await context.WaitForSignalAsync<string>(
                "approval",
                "approve",
                WaitTimeout,
                cancellationToken);
        }
    }

    private sealed class ParentWorkflow : IWorkflow<string, string>
    {
        public async Task<string> RunAsync(
            WorkflowContext context,
            string input,
            CancellationToken cancellationToken)
        {
            var first = await context.StepAsync(
                "parent-step",
                input,
                (value, _) => Task.FromResult($"parent:{value}"),
                cancellationToken: cancellationToken);
            return await context.StartChildAsync<string, string>(
                "child",
                "child",
                "1",
                first,
                cancellationToken);
        }
    }

    private sealed class ChildWorkflow : IWorkflow<string, string>
    {
        public int Calls { get; private set; }

        public async Task<string> RunAsync(
            WorkflowContext context,
            string input,
            CancellationToken cancellationToken)
        {
            Calls++;
            return await context.StepAsync(
                "child-step",
                input,
                (value, _) => Task.FromResult($"child:{value}"),
                cancellationToken: cancellationToken);
        }
    }

    private sealed class FailingChildWorkflow : IWorkflow<string, string>
    {
        public Task<string> RunAsync(
            WorkflowContext context,
            string input,
            CancellationToken cancellationToken) =>
            Task.FromException<string>(
                new InvalidOperationException("child failed"));
    }

    private sealed class FailingParentWorkflow : IWorkflow<string, string>
    {
        public Task<string> RunAsync(
            WorkflowContext context,
            string input,
            CancellationToken cancellationToken) =>
            context.StartChildAsync<string, string>(
                "child",
                "bad-child",
                "1",
                input,
                cancellationToken);
    }

    private sealed class RetryWorkflow : IWorkflow<string, string>
    {
        public int Calls { get; private set; }
        public Guid RunId { get; private set; }

        public async Task<string> RunAsync(
            WorkflowContext context,
            string input,
            CancellationToken cancellationToken)
        {
            RunId = context.WorkflowRunId;
            return await context.StepAsync(
                "unstable",
                input,
                (value, _) =>
                {
                    Calls++;
                    if (Calls == 1)
                        throw new InvalidOperationException("transient");
                    return Task.FromResult($"{value}-ok");
                },
                new StepOptions
                {
                    Retry = new RetryPolicy
                    {
                        MaxAttempts = 2,
                        InitialDelay = TimeSpan.Zero
                    }
                },
                cancellationToken);
        }
    }

    private sealed class FailingWorkflow : IWorkflow<string, string>
    {
        public Task<string> RunAsync(
            WorkflowContext context,
            string input,
            CancellationToken cancellationToken) =>
            context.StepAsync<string>(
                "fail",
                _ => throw new InvalidOperationException("planned failure"),
                cancellationToken: cancellationToken);
    }

    private sealed class DelayWorkflow : IWorkflow<string, string>
    {
        public Guid RunId { get; private set; }

        public async Task<string> RunAsync(
            WorkflowContext context,
            string input,
            CancellationToken cancellationToken)
        {
            RunId = context.WorkflowRunId;
            await context.DelayAsync(
                "short-delay",
                TimeSpan.FromMilliseconds(25),
                cancellationToken);
            return input;
        }
    }

    private sealed class ParallelSameKeyWorkflow : IWorkflow<string, string>
    {
        public int Calls;

        public async Task<string> RunAsync(
            WorkflowContext context,
            string input,
            CancellationToken cancellationToken)
        {
            Task<string> Call() => context.StepAsync(
                "shared",
                input,
                async (value, ct) =>
                {
                    Interlocked.Increment(ref Calls);
                    await Task.Delay(20, ct);
                    return value;
                },
                cancellationToken: cancellationToken);
            var results = await Task.WhenAll(Call(), Call());
            return string.Join(':', results);
        }
    }

    private sealed class RecoveringWorkflow(bool blockSecond)
        : IWorkflow<string, string>
    {
        public int FirstCalls { get; private set; }
        public int SecondCalls { get; private set; }
        public TaskCompletionSource SecondStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<string> RunAsync(
            WorkflowContext context,
            string input,
            CancellationToken cancellationToken)
        {
            var first = await context.StepAsync(
                "first",
                _ =>
                {
                    FirstCalls++;
                    return Task.FromResult("first");
                },
                cancellationToken: cancellationToken);
            return await context.StepAsync(
                "second",
                async (_, ct) =>
                {
                    SecondCalls++;
                    SecondStarted.TrySetResult();
                    if (blockSecond)
                        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                    return $"{first}-second";
                },
                cancellationToken: cancellationToken);
        }
    }

    private sealed class SlowWorkflow : IWorkflow<string, string>
    {
        public int Calls;

        public async Task<string> RunAsync(
            WorkflowContext context,
            string input,
            CancellationToken cancellationToken) =>
            await context.StepAsync(
                "slow",
                async ct =>
                {
                    Interlocked.Increment(ref Calls);
                    await Task.Delay(50, ct);
                    return input;
                },
                cancellationToken: cancellationToken);
    }

    private sealed class TimeoutWorkflow : IWorkflow<string, string>
    {
        public Task<string> RunAsync(
            WorkflowContext context,
            string input,
            CancellationToken cancellationToken) =>
            context.StepAsync(
                "timeout",
                async (_, ct) =>
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                    return input;
                },
                new StepOptions
                {
                    ExecutionTimeout = TimeSpan.FromMilliseconds(25)
                },
                cancellationToken);
    }

    private sealed class ProgressWorkflow : IWorkflow<string, string>
    {
        public Guid RunId { get; private set; }

        public async Task<string> RunAsync(
            WorkflowContext context,
            string input,
            CancellationToken cancellationToken)
        {
            RunId = context.WorkflowRunId;
            await context.EmitAsync(
                WorkflowEventTypes.Progress,
                25,
                cancellationToken);
            await context.EmitAsync(
                WorkflowEventTypes.Progress,
                50,
                cancellationToken);
            await context.EmitAsync(
                WorkflowEventTypes.Progress,
                75,
                cancellationToken);
            return $"{input}-done";
        }
    }

    private sealed class FanOutWorkflow : IWorkflow<string, string>
    {
        public int Calls;
        public Guid RunId { get; private set; }

        public async Task<string> RunAsync(
            WorkflowContext context,
            string input,
            CancellationToken cancellationToken)
        {
            RunId = context.WorkflowRunId;
            var values = input.Split(',');
            var results = await context.FanOutAsync(
                "process",
                values,
                async (value, _, ct) =>
                {
                    Interlocked.Increment(ref Calls);
                    await Task.Delay(5, ct);
                    return value.ToUpperInvariant();
                },
                cancellationToken: cancellationToken);
            return string.Join(':', results);
        }
    }
}
