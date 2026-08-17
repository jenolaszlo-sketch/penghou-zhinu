using FluentAssertions;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using Microsoft.Data.Sqlite;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;
using System.Text.Json;

namespace Penghou.Zhinu.Agents.Tests;

public sealed class WorkflowContextAgentExtensionsTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "penghou-zhinu-agents-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task RunAgentWorkflowAsync_ExecutesGraphAndReturnsOutput()
    {
        var counters = new GraphCounters();
        var checkpointStore = CreateCheckpointStore();
        var graph = BuildGraph(counters, failFirstFinal: false);
        var workflow = new AgentGraphWorkflow(graph, checkpointStore);
        var engine = CreateEngine(workflow, "maf-graph");

        var result = await engine.RunAsync<string, string>(
            "maf-graph",
            "1",
            "input",
            cancellationToken: TestContext.Current.CancellationToken);

        result.Should().Be("b:a:p:input");
        counters.Process.Should().Be(1);
        counters.Stage.Should().Be(1);
        counters.Final.Should().Be(1);
        var run = await engine.GetRunAsync(
            workflow.RunId,
            TestContext.Current.CancellationToken);
        run!.Status.Should().Be(WorkflowStatus.Completed);
        (await engine.GetStepsAsync(
            workflow.RunId,
            TestContext.Current.CancellationToken)).Should().ContainSingle()
            .Which.Status.Should().Be(StepStatus.Completed);
        var checkpoints = await checkpointStore.RetrieveIndexAsync(
            $"{workflow.RunId:D}:agent");
        checkpoints.Should().NotBeEmpty();
    }

    [Fact]
    public async Task RunAgentWorkflowAsync_IsReusedWhenRunIsRestarted()
    {
        var counters = new GraphCounters();
        var checkpointStore = CreateCheckpointStore();
        var graph = BuildGraph(counters, failFirstFinal: false);
        var workflow = new AgentThenDoneWorkflow(graph, checkpointStore);
        var engine = CreateEngine(workflow, "maf-restart");

        var result = await engine.RunAsync<string, string>(
            "maf-restart",
            "1",
            "input",
            cancellationToken: TestContext.Current.CancellationToken);
        result.Should().Be("b:a:p:input|done:input");
        counters.Process.Should().Be(1);

        await engine.RestartStepAsync(
            workflow.RunId,
            "done",
            TestContext.Current.CancellationToken);
        await engine.ExecuteAsync(
            workflow.RunId,
            TestContext.Current.CancellationToken);
        var rerun = await engine.WaitForCompletionAsync<string>(
            workflow.RunId,
            cancellationToken: TestContext.Current.CancellationToken);

        rerun.Should().Be("b:a:p:input|done:input");
        counters.Process.Should().Be(1);
        counters.Stage.Should().Be(1);
        counters.Final.Should().Be(1);
    }

    [Fact]
    public async Task RunAgentWorkflowAsync_ErrorFailsTheRun()
    {
        var counters = new GraphCounters();
        var checkpointStore = CreateCheckpointStore();
        var graph = BuildGraph(counters, failFirstFinal: true);
        var workflow = new AgentGraphWorkflow(graph, checkpointStore);
        var engine = CreateEngine(workflow, "maf-fail");

        var runId = await engine.StartAsync(
            "maf-fail",
            "1",
            "input",
            cancellationToken: TestContext.Current.CancellationToken);
        await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
        await WaitUntilAsync(
            () => HasRunStatusAsync(
                engine,
                runId,
                WorkflowStatus.Failed,
                TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);

        var run = await engine.GetRunAsync(
            runId,
            TestContext.Current.CancellationToken);
        run!.Status.Should().Be(WorkflowStatus.Failed);
        (await engine.GetStepsAsync(
            runId,
            TestContext.Current.CancellationToken)).Should().ContainSingle()
            .Which.Status.Should().Be(StepStatus.Failed);
    }

    [Fact]
    public async Task RunAgentWorkflowAsync_CrashThenRestart_ResumesFromCheckpoint()
    {
        var counters = new GraphCounters();
        var checkpointStore = CreateCheckpointStore();
        var graph = BuildGraph(counters, failFirstFinal: true);
        var workflow = new AgentGraphWorkflow(graph, checkpointStore);
        var engine = CreateEngine(workflow, "maf-resume");

        var runId = await engine.StartAsync(
            "maf-resume",
            "1",
            "input",
            cancellationToken: TestContext.Current.CancellationToken);
        await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
        (await engine.GetRunAsync(
            runId,
            TestContext.Current.CancellationToken))!.Status.Should()
            .Be(WorkflowStatus.Failed);
        counters.Process.Should().Be(1);
        counters.Stage.Should().Be(1);
        counters.Final.Should().Be(1);

        var checkpoints = await checkpointStore.RetrieveIndexAsync($"{runId:D}:agent");
        checkpoints.Should().NotBeEmpty();

        await engine.RestartStepAsync(
            runId,
            "agent",
            TestContext.Current.CancellationToken);
        await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
        var result = await engine.WaitForCompletionAsync<string>(
            runId,
            cancellationToken: TestContext.Current.CancellationToken);

        result.Should().Be("b:a:p:input");
        counters.Process.Should().Be(1);
        counters.Stage.Should().Be(1);
        counters.Final.Should().Be(2);
    }

    private static Workflow BuildGraph(GraphCounters counters, bool failFirstFinal)
    {
        Func<string, string> process = value =>
        {
            counters.Process++;
            return $"p:{value}";
        };
        Func<string, string> stage = value =>
        {
            counters.Stage++;
            return $"a:{value}";
        };
        Func<string, string> final = value =>
        {
            var call = Interlocked.Increment(ref counters.Final);
            if (failFirstFinal && call == 1)
                throw new InvalidOperationException("stage-boom");
            return $"b:{value}";
        };
        var processExecutor = process.BindAsExecutor("process");
        var stageExecutor = stage.BindAsExecutor("stage");
        var finalExecutor = final.BindAsExecutor("final");
        return new WorkflowBuilder(processExecutor)
            .AddEdge(processExecutor, stageExecutor)
            .AddEdge(stageExecutor, finalExecutor)
            .WithOutputFrom(finalExecutor)
            .Build();
    }

    private sealed class GraphCounters
    {
        public int Process;
        public int Stage;
        public int Final;
    }

    private sealed class AgentGraphWorkflow(
        Workflow workflow,
        ICheckpointStore<JsonElement> checkpointStore) : IWorkflow<string, string>
    {
        public Guid RunId { get; private set; }

        public async Task<string> RunAsync(
            WorkflowContext context,
            string input,
            CancellationToken cancellationToken)
        {
            RunId = context.WorkflowRunId;
            return await context.RunAgentWorkflowAsync<string, string>(
                "agent",
                workflow,
                input,
                checkpointStore,
                cancellationToken);
        }
    }

    private sealed class AgentThenDoneWorkflow(
        Workflow workflow,
        ICheckpointStore<JsonElement> checkpointStore) : IWorkflow<string, string>
    {
        public Guid RunId { get; private set; }

        public async Task<string> RunAsync(
            WorkflowContext context,
            string input,
            CancellationToken cancellationToken)
        {
            RunId = context.WorkflowRunId;
            var agent = await context.RunAgentWorkflowAsync<string, string>(
                "agent",
                workflow,
                input,
                checkpointStore,
                cancellationToken);
            var done = await context.StepAsync(
                "done",
                input,
                (_, _) => Task.FromResult($"done:{input}"));
            return $"{agent}|{done}";
        }
    }

    private WorkflowEngine CreateEngine<TWorkflow>(
        TWorkflow workflow,
        string name)
        where TWorkflow : class, IWorkflow<string, string> =>
        new(
            CreateStore(),
            new WorkflowRegistry().Register(name, "1", workflow),
            new ZhinuOptions
            {
                LeaseDuration = TimeSpan.FromSeconds(2),
                LeaseRenewalInterval = TimeSpan.FromMilliseconds(600),
                PollInterval = TimeSpan.FromMilliseconds(10)
            });

    private SqliteWorkflowStore CreateStore() =>
        new(new ZhinuSqliteOptions
        {
            DatabasePath = Path.Combine(root, "zhinu.db"),
            BusyTimeout = TimeSpan.FromSeconds(2)
        });

    private SqliteJsonCheckpointStore CreateCheckpointStore() =>
        new(new ZhinuSqliteOptions
        {
            DatabasePath = Path.Combine(root, "maf-checkpoints.db"),
            BusyTimeout = TimeSpan.FromSeconds(2)
        });

    private static async Task WaitUntilAsync(
        Func<Task<bool>> condition,
        CancellationToken cancellationToken)
    {
        while (!await condition().ConfigureAwait(false))
        {
            await Task.Delay(25, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<bool> HasRunStatusAsync(
        WorkflowEngine engine,
        Guid runId,
        WorkflowStatus status,
        CancellationToken cancellationToken)
    {
        var run = await engine.GetRunAsync(runId, cancellationToken);
        return run?.Status == status;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }
}
