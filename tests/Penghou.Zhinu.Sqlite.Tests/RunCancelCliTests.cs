using FluentAssertions;
using Penghou.Zhinu;
using Penghou.Zhinu.Cli;
using Penghou.Zhinu.Sqlite;

namespace Penghou.Zhinu.Sqlite.Tests;

/// <summary>
/// Operator remediation walkthrough: diagnose a parked signal wait, cancel
/// the stuck run with actor/reason audit, verify the terminal state, and
/// prove repeat and completed-run cancellation are explicit no-ops.
/// </summary>
public sealed class RunCancelCliTests : WorkflowEngineTestBase
{
    [Fact]
    public async Task Walkthrough_CancelStuckRunAndVerify()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = Path.Combine(root, "cancel.db");
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

        var waiting = await RunCliAsync("runs", "why-waiting", runId.ToString("D"));
        waiting.Code.Should().Be(0);
        waiting.Output.Should().Contain("release");

        var cancelled = await RunCliAsync(
            "runs", "cancel", runId.ToString("D"), "--actor", "operator", "--reason", "stuck review");
        cancelled.Code.Should().Be(0);
        cancelled.Output.Should().Contain("cancelled").And.Contain(runId.ToString("D"));

        var show = await RunCliAsync("runs", "show", runId.ToString("D"));
        show.Code.Should().Be(0);
        show.Output.Should().Contain("Cancelled");

        var again = await RunCliAsync("runs", "cancel", runId.ToString("D"));
        again.Code.Should().Be(0);
        again.Output.Should().Contain("already").And.Contain("Cancelled");

        var json = await RunCliAsync("--format", "json", "runs", "cancel", runId.ToString("D"));
        json.Code.Should().Be(0);
        json.Output.Should().Contain("already").And.Contain("Cancelled");

        var missing = Guid.NewGuid();
        var missingResult = await RunCliAsync("runs", "cancel", missing.ToString("D"));
        missingResult.Code.Should().Be(2);
        missingResult.Output.Should().Contain("not found");

        async Task<(int Code, string Output)> RunCliAsync(params string[] command)
        {
            var writer = new StringWriter();
            var full = new List<string> { "--db", database };
            full.AddRange(command);
            var code = await CliApp.RunAsync([.. full], writer);
            return (code, writer.ToString());
        }
    }

    [Fact]
    public async Task Cancel_CompletedRunIsExplicitNoOp()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = Path.Combine(root, "cancel-completed.db");
        var store = new SqliteWorkflowStore(new ZhinuSqliteOptions
        {
            DatabasePath = database,
            BusyTimeout = TimeSpan.FromSeconds(2),
            Pooling = false
        });
        await using var engine = new WorkflowEngine(
            store,
            new WorkflowRegistry().Register("instant", "1", new InstantWorkflow()),
            new ZhinuOptions { PollInterval = TimeSpan.FromMilliseconds(10) });
        var runId = await engine.StartAsync("instant", "1", "go", cancellationToken: ct);
        await engine.ExecuteAsync(runId, ct);
        var result = await engine.WaitForCompletionAsync<string>(runId, cancellationToken: ct);
        result.Should().Be("go");

        var writer = new StringWriter();
        var code = await CliApp.RunAsync(
            ["--db", database, "runs", "cancel", runId.ToString("D")], writer);
        code.Should().Be(0);
        writer.ToString().Should().Contain("already").And.Contain("Completed");
    }

    private sealed class ApprovalWorkflow : IWorkflow<string, string>
    {
        public Task<string> RunAsync(
            WorkflowContext context, string input, CancellationToken cancellationToken) =>
            context.WaitForSignalAsync<string>(
                "approve", "release", cancellationToken: cancellationToken);
    }

    private sealed class InstantWorkflow : IWorkflow<string, string>
    {
        public Task<string> RunAsync(
            WorkflowContext context, string input, CancellationToken cancellationToken) =>
            Task.FromResult(input);
    }
}
