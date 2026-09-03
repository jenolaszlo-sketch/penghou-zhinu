using FluentAssertions;

namespace Penghou.Zhinu.Sqlite.Tests;

public sealed class RollbackTests : WorkflowEngineTestBase
{

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
    public async Task RollbackAsync_CompensationTimeout_PersistsTypedFailure()
    {
        var forward = new TimeoutCompensationWorkflow(blockCompensation: false);
        var engine = CreateEngine(forward, "rollback-timeout");
        await engine.RunAsync<string, string>(
            "rollback-timeout",
            "1",
            "x",
            cancellationToken: TestContext.Current.CancellationToken);

        var rollback = new TimeoutCompensationWorkflow(blockCompensation: true);
        var rollbackEngine = CreateEngine(rollback, "rollback-timeout");
        Func<Task> action = () => rollbackEngine.RollbackAsync(
            forward.RunId,
            cancellationToken: TestContext.Current.CancellationToken);

        await action.Should().ThrowAsync<RollbackFailedException>();
        var compensations = await rollbackEngine.GetCompensationsAsync(
            forward.RunId,
            TestContext.Current.CancellationToken);
        compensations.Should().ContainSingle();
        var compensation = compensations.Single();
        compensation.Status.Should().Be(CompensationStatus.Failed);
        compensation.Error!.Type.Should().Be(
            typeof(WorkflowTimeoutException).FullName);
        compensation.Error.Message.Should().Contain("execution timeout");
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
    public async Task TryCreateAndClaimRollbackAndRestartAsync_ConcurrentClaimsPersistOneOperation()
    {
        var workflow = new RollbackWorkflow();
        var engine = CreateEngine(workflow, "rollback");
        await engine.RunAsync<string, string>(
            "rollback",
            "1",
            "x",
            cancellationToken: TestContext.Current.CancellationToken);
        var runId = workflow.RunId;
        var now = DateTimeOffset.UtcNow;
        var stores = new[] { CreateStore(), CreateStore() };
        var claims = stores.Select((store, index) =>
            store.TryCreateAndClaimRollbackAndRestartAsync(
                new WorkflowRunOperation
                {
                    OperationId = Guid.NewGuid(),
                    WorkflowRunId = runId,
                    OperationType = "rollback-and-restart",
                    Status = WorkflowOperationStatus.Requested,
                    PayloadJson = null,
                    CreatedAt = now.AddTicks(index),
                    UpdatedAt = now.AddTicks(index)
                },
                $"owner-{index}",
                now,
                now.AddMinutes(1),
                TestContext.Current.CancellationToken).AsTask());

        var results = await Task.WhenAll(claims);

        results.Count(result => result.HasValue).Should().Be(1);
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            $"Data Source={Path.Combine(root, "zhinu.db")}");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM workflow_run_operations WHERE workflow_run_id = $runId;";
        command.Parameters.AddWithValue("$runId", runId.ToString("D"));
        Convert.ToInt64(await command.ExecuteScalarAsync(
            TestContext.Current.CancellationToken)).Should().Be(1);
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

    private sealed class TimeoutCompensationWorkflow(bool blockCompensation)
        : IWorkflow<string, string>
    {
        public Guid RunId { get; private set; }

        public async Task<string> RunAsync(
            WorkflowContext context,
            string input,
            CancellationToken cancellationToken)
        {
            RunId = context.WorkflowRunId;
            return await context.StepAsync(
                "reserve",
                input,
                (value, _) => Task.FromResult($"reserved:{value}"),
                new StepOptions
                {
                    ExecutionTimeout = TimeSpan.FromMilliseconds(25)
                },
                cancellationToken,
                compensation: async (_, _, token) =>
                {
                    if (blockCompensation)
                        await Task.Delay(Timeout.InfiniteTimeSpan, token);
                });
        }
    }
}
