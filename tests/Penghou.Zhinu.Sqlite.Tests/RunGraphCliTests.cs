using FluentAssertions;
using Penghou.Zhinu;
using Penghou.Zhinu.Cli;
using Penghou.Zhinu.Sqlite;

namespace Penghou.Zhinu.Sqlite.Tests;

/// <summary>
/// Read-only run graph projection over recorded dependency edges, step
/// attempts, current leases, and parked waits. The projection never retrieves
/// payloads, and reopening the store (process restart) yields the same graph.
/// </summary>
public sealed class RunGraphCliTests : WorkflowEngineTestBase
{
    [Fact]
    public async Task Graph_ProjectsEdgesStepsLeasesAndWaits()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = Path.Combine(root, "graph.db");
        var store = new SqliteWorkflowStore(new ZhinuSqliteOptions
        {
            DatabasePath = database,
            BusyTimeout = TimeSpan.FromSeconds(2),
            Pooling = false
        });
        var workflow = new ChainedWorkflow();
        await using (var engine = new WorkflowEngine(
            store,
            new WorkflowRegistry().Register("chained", "1", workflow),
            new ZhinuOptions { PollInterval = TimeSpan.FromMilliseconds(10) }))
        {
            var runId = await engine.StartAsync("chained", "1", "seed", cancellationToken: ct);
            await engine.ExecuteAsync(runId, ct);
            workflow.RunId = runId;
        }

        var text = await RunCliAsync("runs", "graph", workflow.RunId.ToString("D"));
        text.Code.Should().Be(0);
        text.Output.Should().Contain($"run: {workflow.RunId:D}");
        text.Output.Should().Contain("steps (2):");
        text.Output.Should().Contain("first").And.Contain("second");
        text.Output.Should().Contain("edges (1):");
        text.Output.Should().Contain("second -> first");
        text.Output.Should().Contain("lease -");
        text.Output.Should().Contain("waits (0):");
        text.Output.Should().Contain("fingerprint:");
        // The graph is a structural projection: payloads are never retrieved.
        text.Output.Should().NotContain("seed");

        // Reopening the store (a new CLI process) yields an identical graph.
        var reopened = await RunCliAsync("runs", "graph", workflow.RunId.ToString("D"));
        reopened.Code.Should().Be(0);
        reopened.Output.Should().Be(text.Output);

        var json = await RunCliAsync("--format", "json", "runs", "graph", workflow.RunId.ToString("D"));
        json.Code.Should().Be(0);
        json.Output.Should().Contain("\"Nodes\"").And.Contain("\"Edges\"");

        var missing = await RunCliAsync("runs", "graph", Guid.NewGuid().ToString("D"));
        missing.Code.Should().Be(2);
        missing.Output.Should().Contain("not found");

        async Task<(int Code, string Output)> RunCliAsync(params string[] command)
        {
            var writer = new StringWriter();
            var full = new List<string> { "--db", database };
            full.AddRange(command);
            var code = await CliApp.RunAsync([.. full], writer);
            return (code, writer.ToString());
        }
    }

    private sealed class ChainedWorkflow : IWorkflow<string, string>
    {
        public Guid RunId { get; set; }

        public async Task<string> RunAsync(
            WorkflowContext context, string input, CancellationToken cancellationToken)
        {
            RunId = context.WorkflowRunId;
            var first = await context.StepAsync(
                "first",
                input,
                (value, _) => Task.FromResult(value + "-1"),
                cancellationToken: cancellationToken);
            using (context.DependsOn("first"))
            {
                return await context.StepAsync(
                    "second",
                    first,
                    (value, _) => Task.FromResult(value + "-2"),
                    cancellationToken: cancellationToken);
            }
        }
    }
}
