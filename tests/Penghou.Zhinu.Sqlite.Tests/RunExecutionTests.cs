using FluentAssertions;

namespace Penghou.Zhinu.Sqlite.Tests;

public sealed class RunExecutionTests : WorkflowEngineTestBase
{

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
}
