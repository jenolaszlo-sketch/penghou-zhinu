using FluentAssertions;
using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace Penghou.Zhinu.Sqlite.Tests;

public sealed class DurableLoopTimeLimitTests : WorkflowEngineTestBase
{
    [Fact]
    public void TimeBudget_MustBePositive()
    {
        var zero = () => new LoopOptions(1) { TimeBudget = TimeSpan.Zero };
        var negative = () => new LoopOptions(1) { TimeBudget = TimeSpan.FromTicks(-1) };

        zero.Should().Throw<ArgumentOutOfRangeException>()
            .Which.ParamName.Should().Be(nameof(LoopOptions.TimeBudget));
        negative.Should().Throw<ArgumentOutOfRangeException>()
            .Which.ParamName.Should().Be(nameof(LoopOptions.TimeBudget));
    }

    [Fact]
    public async Task ExpiredDeadline_FailsBeforeEnteringBodyWithTypedEvidence()
    {
        var now = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
        var clock = new AdjustableTimeProvider(now);
        var workflow = new TimeLimitedLoopWorkflow(
            new LoopOptions(10) { Deadline = now.AddMinutes(-1) });
        var engine = CreateEngine(workflow, "loop-deadline", clock);

        var action = () => engine.RunAsync<string, string>(
            "loop-deadline",
            "1",
            "ignored",
            cancellationToken: TestContext.Current.CancellationToken);

        var failure = await action.Should()
            .ThrowAsync<WorkflowExecutionFailedException>();
        failure.Which.Error.Type.Should().Be(
            typeof(LoopLimitExceededException).FullName);
        failure.Which.Error.Message.Should().Contain("exceeded its deadline");
        workflow.BodyCalls.Should().Be(0);

        var progress = await engine.GetLoopProgressAsync(
            workflow.RunId,
            WorkflowLoopReference.Root("refinement"),
            TestContext.Current.CancellationToken);
        progress!.LimitsStep!.Status.Should().Be(StepStatus.Completed);
        progress.LimitStep!.Status.Should().Be(StepStatus.Completed);
        progress.Iterations.Should().BeEmpty();

        var events = await engine.GetEventsAsync(
            workflow.RunId,
            cancellationToken: TestContext.Current.CancellationToken);
        events.Count(item => item.EventType == WorkflowEventTypes.LoopLimitExceeded)
            .Should().Be(1);
        events.Single(item => item.EventType == WorkflowEventTypes.LoopLimitExceeded)
            .DataJson.Should().Contain(nameof(LoopLimitKind.Deadline));
    }

    [Fact]
    public async Task TimeBudgetExpiringDuringBody_DoesNotCommitIteration()
    {
        var now = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
        var clock = new AdjustableTimeProvider(now);
        var workflow = new TimeLimitedLoopWorkflow(
            new LoopOptions(10)
            {
                Deadline = now.AddHours(1),
                TimeBudget = TimeSpan.FromMinutes(5)
            },
            afterBody: () => clock.Advance(TimeSpan.FromMinutes(6)));
        var engine = CreateEngine(workflow, "loop-budget-boundary", clock);

        Func<Task<string>> action = () => engine.RunAsync<string, string>(
            "loop-budget-boundary",
            "1",
            "ignored",
            cancellationToken: TestContext.Current.CancellationToken);

        await action.Should().ThrowAsync<WorkflowExecutionFailedException>();

        var steps = await engine.GetStepsAsync(
            workflow.RunId,
            TestContext.Current.CancellationToken);
        steps.Should().Contain(step =>
            step.StepKey == "$loop/refinement/1/body/advance" &&
            step.Status == StepStatus.Completed);
        steps.Should().NotContain(step =>
            step.StepKey == "$loop/refinement/1/commit");
        var limitEvent = (await engine.GetEventsAsync(
                workflow.RunId,
                cancellationToken: TestContext.Current.CancellationToken))
            .Single(item => item.EventType == WorkflowEventTypes.LoopLimitExceeded);
        limitEvent.DataJson.Should().Contain(nameof(LoopLimitKind.TimeBudget));
    }

    [Fact]
    public async Task TimeBudget_IsResolvedOnceAndDoesNotResetAfterInterruption()
    {
        var startedAt = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
        var clock = new AdjustableTimeProvider(startedAt);
        var interrupted = new InterruptibleBudgetLoopWorkflow();
        var engine = CreateEngine(interrupted, "loop-budget-restart", clock);
        var runId = await engine.StartAsync(
            "loop-budget-restart",
            "1",
            "ignored",
            cancellationToken: TestContext.Current.CancellationToken);
        using var interruption = new CancellationTokenSource();

        var execution = engine.ExecuteAsync(runId, interruption.Token);
        await interrupted.SecondIterationEntered.Task.WaitAsync(
            TestContext.Current.CancellationToken);
        interruption.Cancel();
        await execution;

        (await engine.GetRunAsync(runId, TestContext.Current.CancellationToken))!
            .Status.Should().Be(WorkflowStatus.Running);
        clock.Advance(TimeSpan.FromMinutes(11));

        var resumed = new TimeLimitedLoopWorkflow(InterruptibleBudgetLoopWorkflow.Options);
        var reopened = CreateEngine(resumed, "loop-budget-restart", clock);
        await reopened.ExecuteAsync(runId, TestContext.Current.CancellationToken);

        var run = await reopened.GetRunAsync(
            runId,
            TestContext.Current.CancellationToken);
        run!.Status.Should().Be(WorkflowStatus.Failed);
        run.Error!.Type.Should().Be(typeof(LoopLimitExceededException).FullName);
        run.Error.Message.Should().Contain("time budget");
        resumed.BodyCalls.Should().Be(0);

        var steps = await reopened.GetStepsAsync(
            runId,
            TestContext.Current.CancellationToken);
        var limitsStep = steps.Single(step =>
            step.StepKey == "$loop/refinement/limits");
        using var resolutionDocument = JsonDocument.Parse(limitsStep.InputJson!);
        resolutionDocument.RootElement.GetProperty("budgetStartedAt")
            .GetDateTimeOffset().Should().Be(startedAt);
        using var limitsDocument = JsonDocument.Parse(limitsStep.OutputJson!);
        limitsDocument.RootElement.GetProperty("effectiveDeadline")
            .GetDateTimeOffset().Should().Be(startedAt.AddMinutes(10));
        steps.Should().Contain(step =>
            step.StepKey == "$loop/refinement/1/commit" &&
            step.Status == StepStatus.Completed);
        steps.Should().Contain(step =>
            step.StepKey == "$loop/refinement/2/body/advance" &&
            step.Status == StepStatus.Completed);
        steps.Should().NotContain(step => step.StepKey == "$loop/refinement/2/commit");
        var events = await reopened.GetEventsAsync(
            runId,
            cancellationToken: TestContext.Current.CancellationToken);
        events.Count(item => item.EventType == WorkflowEventTypes.LoopLimitExceeded)
            .Should().Be(1);
    }

    [Fact]
    public async Task ExpiredDeadline_DoesNotBlockRollbackReplay()
    {
        var now = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
        var clock = new AdjustableTimeProvider(now);
        var options = new LoopOptions(3) { Deadline = now.AddMinutes(1) };
        var forward = new CompensatingTimeLimitedLoopWorkflow(options);
        var engine = CreateEngine(forward, "loop-deadline-rollback", clock);
        await engine.RunAsync<string, string>(
            "loop-deadline-rollback",
            "1",
            "ignored",
            cancellationToken: TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromMinutes(2));

        var rollback = new CompensatingTimeLimitedLoopWorkflow(options);
        var reopened = CreateEngine(rollback, "loop-deadline-rollback", clock);
        await reopened.RollbackAsync(
            forward.RunId,
            cancellationToken: TestContext.Current.CancellationToken);

        rollback.CompensatedStates.Should().Equal(2, 1);
        (await reopened.GetRunAsync(
            forward.RunId,
            TestContext.Current.CancellationToken))!.Status
            .Should().Be(WorkflowStatus.Compensated);
    }

    private WorkflowEngine CreateEngine<TWorkflow>(
        TWorkflow workflow,
        string name,
        TimeProvider timeProvider)
        where TWorkflow : class, IWorkflow<string, string>
    {
        SqliteConnection.ClearAllPools();
        var store = new SqliteWorkflowStore(new ZhinuSqliteOptions
        {
            DatabasePath = Path.Combine(root, "zhinu.db"),
            BusyTimeout = TimeSpan.FromSeconds(2),
            Pooling = false,
            TimeProvider = timeProvider
        });
        return new WorkflowEngine(
            store,
            new WorkflowRegistry().Register(name, "1", workflow),
            new ZhinuOptions
            {
                LeaseDuration = TimeSpan.FromSeconds(2),
                LeaseRenewalInterval = TimeSpan.FromMilliseconds(500),
                PollInterval = TimeSpan.FromMilliseconds(10)
            },
            timeProvider: timeProvider);
    }

    private sealed class TimeLimitedLoopWorkflow(
        LoopOptions options,
        Action? afterBody = null) : IWorkflow<string, string>
    {
        public Guid RunId { get; private set; }

        public int BodyCalls { get; private set; }

        public async Task<string> RunAsync(
            WorkflowContext context,
            string input,
            CancellationToken cancellationToken)
        {
            RunId = context.WorkflowRunId;
            return await context.LoopAsync(
                "refinement",
                "0",
                _ => true,
                async (iteration, token) =>
                {
                    BodyCalls++;
                    var next = await iteration.StepAsync(
                        "advance",
                        (_, _) => Task.FromResult(
                            (int.Parse(iteration.State) + 1).ToString()),
                        cancellationToken: token);
                    afterBody?.Invoke();
                    return iteration.Continue(next);
                },
                options,
                cancellationToken);
        }
    }

    private sealed class InterruptibleBudgetLoopWorkflow : IWorkflow<string, string>
    {
        public static LoopOptions Options { get; } = new(10)
        {
            TimeBudget = TimeSpan.FromMinutes(10)
        };

        public TaskCompletionSource SecondIterationEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<string> RunAsync(
            WorkflowContext context,
            string input,
            CancellationToken cancellationToken) =>
            await context.LoopAsync(
                "refinement",
                "0",
                _ => true,
                async (iteration, token) =>
                {
                    var next = await iteration.StepAsync(
                        "advance",
                        (_, _) => Task.FromResult(
                            (int.Parse(iteration.State) + 1).ToString()),
                        cancellationToken: token);
                    if (iteration.Iteration == 2)
                    {
                        SecondIterationEntered.TrySetResult();
                        await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    }
                    return iteration.Continue(next);
                },
                Options,
                cancellationToken);
    }

    private sealed class CompensatingTimeLimitedLoopWorkflow(LoopOptions options)
        : IWorkflow<string, string>
    {
        public Guid RunId { get; private set; }

        public List<int> CompensatedStates { get; } = [];

        public async Task<string> RunAsync(
            WorkflowContext context,
            string input,
            CancellationToken cancellationToken)
        {
            RunId = context.WorkflowRunId;
            var result = await context.LoopAsync(
                "compensating",
                0,
                state => state < 2,
                async (iteration, token) =>
                {
                    var next = await iteration.StepAsync(
                        "advance",
                        iteration.State,
                        (state, _, _) => Task.FromResult(state + 1),
                        cancellationToken: token,
                        compensation: (state, _, _) =>
                        {
                            CompensatedStates.Add(state);
                            return Task.CompletedTask;
                        });
                    return iteration.Continue(next);
                },
                options,
                cancellationToken);
            return result.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    private sealed class AdjustableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private readonly object sync = new();
        private DateTimeOffset value = utcNow;

        public override DateTimeOffset GetUtcNow()
        {
            lock (sync)
                return value;
        }

        public void Advance(TimeSpan duration)
        {
            lock (sync)
                value = value.Add(duration);
        }
    }
}
