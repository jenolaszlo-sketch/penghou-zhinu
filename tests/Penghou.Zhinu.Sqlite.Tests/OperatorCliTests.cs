using FluentAssertions;
using Penghou.Zhinu;
using Penghou.Zhinu.Cli;
using Penghou.Zhinu.Sqlite;

namespace Penghou.Zhinu.Sqlite.Tests;

/// <summary>
/// Operator walkthrough against a temporary database: diagnose a blocked
/// signal wait, supply the signal, inspect the completed result, and preview
/// interventions, all without touching SQLite directly.
/// </summary>
public sealed class OperatorCliTests : WorkflowEngineTestBase
{
    [Fact]
    public async Task Walkthrough_DiagnoseSignalInspectPreview()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = Path.Combine(root, "walkthrough.db");
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

        var list = await RunCliAsync("runs", "list");
        list.Should().Contain("approval").And.Contain(runId.ToString("D"));

        var show = await RunCliAsync("runs", "show", runId.ToString("D"));
        show.Should().Contain("status:").And.Contain("hidden");

        var why = await RunCliAsync("runs", "why-waiting", runId.ToString("D"));
        why.Should().Contain("release");

        var signaled = await RunCliAsync(
            "runs", "signal", runId.ToString("D"), "release", "--data", "\"approved\"");
        signaled.Should().Contain("buffered signal");

        await engine.ExecuteAsync(runId, ct);
        var result = await engine.WaitForCompletionAsync<string>(runId, cancellationToken: ct);
        result.Should().Be("\"approved\"");

        var events = await RunCliAsync("runs", "events", runId.ToString("D"));
        events.Should().Contain("signal-delivered").And.Contain("hidden");

        var eventsOpen = await RunCliAsync(
            "runs", "events", runId.ToString("D"), "--include-payloads");
        eventsOpen.Should().Contain("approved");

        var restart = await RunCliAsync(
            "runs", "restart-preview", runId.ToString("D"), "approve");
        restart.Should().Contain("approve");

        var retention = await RunCliAsync("runs", "retention-preview", "--older-than-days", "7");
        retention.Should().Contain("eligible: 0");

        var json = await RunCliAsync("--format", "json", "runs", "show", runId.ToString("D"));
        json.Should().Contain("\"Status\"");

        async Task<string> RunCliAsync(params string[] command)
        {
            var writer = new StringWriter();
            var full = new List<string> { "--db", database };
            full.AddRange(command);
            var code = await CliApp.RunAsync([.. full], writer);
            code.Should().Be(0);
            return writer.ToString();
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
