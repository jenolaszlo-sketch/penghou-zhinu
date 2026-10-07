using FluentAssertions;
using Penghou.Zhinu;
using Penghou.Zhinu.Cli;
using Penghou.Zhinu.Sqlite;

namespace Penghou.Zhinu.Sqlite.Tests;

/// <summary>
/// Operator waiting walkthrough: block on a parked run until it completes
/// instead of polling show by hand, with bounded timeouts, immediate return
/// for terminal runs, and honest errors for bad input and missing runs.
/// </summary>
public sealed class RunWaitCliTests : WorkflowEngineTestBase
{
    [Fact]
    public async Task Walkthrough_WaitForParkedRunToComplete()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = Path.Combine(root, "wait.db");
        var store = new SqliteWorkflowStore(new ZhinuSqliteOptions
        {
            DatabasePath = database,
            BusyTimeout = TimeSpan.FromSeconds(2),
            Pooling = false
        });
        await using var engine = new WorkflowEngine(
            store,
            new WorkflowRegistry().Register("approval", "1", new ApprovalWorkflow()),
            new ZhinuOptions { PollInterval = TimeSpan.FromMilliseconds(10) });
        var runId = await engine.StartAsync("approval", "1", "doc-42", cancellationToken: ct);
        await engine.ExecuteAsync(runId, ct);

        var timeout = await RunCliAsync(
            "runs", "wait", runId.ToString("D"), "--timeout-seconds", "2");
        timeout.Code.Should().Be(2);
        timeout.Output.Should().Contain("timeout").And.Contain("Running");

        var signaled = await RunCliAsync(
            "runs", "signal", runId.ToString("D"), "release", "--data", "\"approved\"");
        signaled.Code.Should().Be(0);

        await engine.ExecuteAsync(runId, ct);
        var completed = await RunCliAsync(
            "runs", "wait", runId.ToString("D"), "--timeout-seconds", "30");
        completed.Code.Should().Be(0);
        completed.Output.Should().Contain("terminal").And.Contain("Completed");

        var immediate = await RunCliAsync("runs", "wait", runId.ToString("D"));
        immediate.Code.Should().Be(0);
        immediate.Output.Should().Contain("terminal").And.Contain("Completed");

        var json = await RunCliAsync("--format", "json", "runs", "wait", runId.ToString("D"));
        json.Code.Should().Be(0);
        json.Output.Should().Contain("terminal");

        var badTimeout = await RunCliAsync(
            "runs", "wait", runId.ToString("D"), "--timeout-seconds", "0");
        badTimeout.Code.Should().Be(1);
        badTimeout.Output.Should().Contain("timeout-seconds");

        var missingRun = Guid.NewGuid();
        var missing = await RunCliAsync("runs", "wait", missingRun.ToString("D"));
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

    private sealed class ApprovalWorkflow : IWorkflow<string, string>
    {
        public Task<string> RunAsync(
            WorkflowContext context, string input, CancellationToken cancellationToken) =>
            context.WaitForSignalAsync<string>(
                "approve", "release", cancellationToken: cancellationToken);
    }
}
