using FluentAssertions;
using Penghou.Zhinu;
using Penghou.Zhinu.Cli;
using Penghou.Zhinu.Sqlite;

namespace Penghou.Zhinu.Sqlite.Tests;

/// <summary>
/// Operator remediation walkthrough: a failed step is previewed, restarted
/// with an idempotent operation id, replayed safely on retry, re-executed
/// to observe the outcome, and refused without an operation id.
/// </summary>
public sealed class RunRestartCliTests : WorkflowEngineTestBase
{
    [Fact]
    public async Task Walkthrough_PreviewRestartReexecute()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = Path.Combine(root, "restart.db");
        var store = new SqliteWorkflowStore(new ZhinuSqliteOptions
        {
            DatabasePath = database,
            BusyTimeout = TimeSpan.FromSeconds(2),
            Pooling = false
        });
        await using var engine = new WorkflowEngine(
            store,
            new WorkflowRegistry().Register("fragile", "1", new FragileWorkflow()),
            new ZhinuOptions { PollInterval = TimeSpan.FromMilliseconds(10) });
        var runId = await engine.StartAsync("fragile", "1", "go", cancellationToken: ct);
        try { await engine.ExecuteAsync(runId, ct); } catch { }

        var preview = await RunCliAsync("runs", "restart-preview", runId.ToString("D"), "work");
        preview.Code.Should().Be(0);
        preview.Output.Should().Contain("work");

        var operationId = Guid.NewGuid().ToString("D");
        var restarted = await RunCliAsync(
            "runs", "restart", runId.ToString("D"), "work",
            "--operation-id", operationId, "--actor", "operator", "--reason", "retry after fix");
        restarted.Code.Should().Be(0);
        restarted.Output.Should().Contain("applied").And.Contain("work");

        var replayed = await RunCliAsync(
            "runs", "restart", runId.ToString("D"), "work",
            "--operation-id", operationId, "--actor", "operator", "--reason", "retry after fix");
        replayed.Code.Should().Be(0);
        replayed.Output.Should().Contain("replayed");

        var json = await RunCliAsync(
            "--format", "json", "runs", "restart", runId.ToString("D"), "work",
            "--operation-id", operationId, "--actor", "operator", "--reason", "retry after fix");
        json.Code.Should().Be(0);
        json.Output.Should().Contain("replayed");

        var conflict = await RunCliAsync(
            "runs", "restart", runId.ToString("D"), "work",
            "--operation-id", operationId, "--actor", "operator", "--reason", "different intent");
        conflict.Code.Should().Be(1);
        conflict.Output.Should().Contain("error:");

        var show = await RunCliAsync("runs", "show", runId.ToString("D"));
        show.Code.Should().Be(0);
        show.Output.Should().Contain("Pending");

        try { await engine.ExecuteAsync(runId, ct); } catch { }
        var again = await RunCliAsync("runs", "show", runId.ToString("D"));
        again.Code.Should().Be(0);
        again.Output.Should().Contain("Failed");

        var missingId = await RunCliAsync("runs", "restart", runId.ToString("D"), "work");
        missingId.Code.Should().Be(1);
        missingId.Output.Should().Contain("operation-id");

        var missingRun = Guid.NewGuid();
        var missing = await RunCliAsync(
            "runs", "restart", missingRun.ToString("D"), "work", "--operation-id", Guid.NewGuid().ToString("D"));
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

    private sealed class FragileWorkflow : IWorkflow<string, string>
    {
        public async Task<string> RunAsync(
            WorkflowContext context, string input, CancellationToken cancellationToken) =>
            await context.StepAsync<string>(
                "work",
                async ct =>
                {
                    await Task.Delay(1, ct);
                    throw new InvalidOperationException("boom");
                },
                cancellationToken: cancellationToken);
    }
}
