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
            TestContext.Current.CancellationToken);
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
            TestContext.Current.CancellationToken);
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
        await store.TryClaimRunAsync(
            runId,
            "owner",
            now,
            now.AddMinutes(1),
            TestContext.Current.CancellationToken);
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
                LeaseExpiresAt = now.AddMinutes(1)
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
            LeaseExpiresAt = now.AddMinutes(1)
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
    public async Task InitializeAsync_MigratesV1DatabaseWithoutLosingRuns()
    {
        var databasePath = Path.Combine(root, "zhinu.db");
        Directory.CreateDirectory(root);
        var now = DateTimeOffset.UtcNow;
        var runId = Guid.NewGuid();
        var connectionString =
            new SqliteConnectionStringBuilder { DataSource = databasePath }
                .ToString();
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    CREATE TABLE zhinu_schema(version INTEGER NOT NULL);
                    INSERT INTO zhinu_schema(version) VALUES (1);
                    CREATE TABLE workflow_runs
                    (
                        id TEXT PRIMARY KEY,
                        workflow_name TEXT NOT NULL,
                        workflow_version TEXT NOT NULL,
                        status INTEGER NOT NULL,
                        input_json TEXT NULL,
                        input_type TEXT NULL,
                        output_json TEXT NULL,
                        output_type TEXT NULL,
                        error_json TEXT NULL,
                        created_at TEXT NOT NULL,
                        updated_at TEXT NOT NULL,
                        completed_at TEXT NULL,
                        lease_owner TEXT NULL,
                        lease_expires_at TEXT NULL
                    );
                    CREATE TABLE workflow_steps
                    (
                        id TEXT PRIMARY KEY,
                        workflow_run_id TEXT NOT NULL,
                        step_key TEXT NOT NULL,
                        status INTEGER NOT NULL,
                        attempt INTEGER NOT NULL,
                        input_json TEXT NULL,
                        input_type TEXT NULL,
                        input_hash TEXT NULL,
                        output_json TEXT NULL,
                        output_type TEXT NULL,
                        error_json TEXT NULL,
                        created_at TEXT NOT NULL,
                        started_at TEXT NULL,
                        completed_at TEXT NULL,
                        available_at TEXT NULL,
                        lease_owner TEXT NULL,
                        lease_expires_at TEXT NULL,
                        UNIQUE(workflow_run_id, step_key),
                        FOREIGN KEY(workflow_run_id) REFERENCES workflow_runs(id) ON DELETE CASCADE
                    );
                    CREATE TABLE workflow_events
                    (
                        sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                        workflow_run_id TEXT NOT NULL,
                        step_key TEXT NULL,
                        event_type TEXT NOT NULL,
                        timestamp TEXT NOT NULL,
                        attempt INTEGER NULL,
                        data_json TEXT NULL,
                        FOREIGN KEY(workflow_run_id) REFERENCES workflow_runs(id) ON DELETE CASCADE
                    );
                    INSERT INTO workflow_runs
                    (id, workflow_name, workflow_version, status, input_json,
                     input_type, output_json, output_type, error_json, created_at,
                     updated_at, completed_at, lease_owner, lease_expires_at)
                    VALUES
                    ($id, 'manual', '1', $pending, NULL, NULL, NULL, NULL, NULL,
                     $now, $now, NULL, NULL, NULL);
                    """;
                command.Parameters.AddWithValue("$id", runId.ToString("D"));
                command.Parameters.AddWithValue("$pending", (int)WorkflowStatus.Pending);
                command.Parameters.AddWithValue("$now", now.ToString("O"));
                await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }
        }

        SqliteConnection.ClearAllPools();
        var store = CreateStore();
        await store.InitializeAsync(TestContext.Current.CancellationToken);
        var run = await store.GetRunAsync(
            runId,
            TestContext.Current.CancellationToken);

        run.Should().NotBeNull();
        run!.Status.Should().Be(WorkflowStatus.Pending);
        var version = await ScalarAsync(databasePath, TestContext.Current.CancellationToken);
        version.Should().Be(2L);
    }

    private static async Task<long> ScalarAsync(
        string databasePath,
        CancellationToken cancellationToken)
    {
        var connectionString =
            new SqliteConnectionStringBuilder { DataSource = databasePath }
                .ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT version FROM zhinu_schema LIMIT 1;";
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private WorkflowEngine CreateEngine<TWorkflow>(
        TWorkflow workflow,
        string name,
        TimeSpan? leaseDuration = null)
        where TWorkflow : class, IWorkflow<string, string>
    {
        var registry = new WorkflowRegistry()
            .Register(name, "1", workflow);
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

    private SqliteWorkflowStore CreateStore() =>
        new(new ZhinuSqliteOptions
        {
            DatabasePath = Path.Combine(root, "zhinu.db"),
            BusyTimeout = TimeSpan.FromSeconds(2)
        });

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
}
