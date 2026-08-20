using FluentAssertions;

namespace Penghou.Zhinu.Sqlite.Tests;

public sealed class RestartStepTests : WorkflowEngineTestBase
{

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
}
