using FluentAssertions;
using Penghou.Zhinu;
using Penghou.Zhinu.Cli;
using Penghou.Zhinu.Sqlite;

namespace Penghou.Zhinu.Sqlite.Tests;

/// <summary>
/// The run list exposes the query surface's workflow name and version filters,
/// so operators can narrow a large database without reading and filtering the
/// full page by hand.
/// </summary>
public sealed class RunListFilterCliTests : WorkflowEngineTestBase
{
    [Fact]
    public async Task List_FiltersByWorkflowNameAndVersion()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = Path.Combine(root, "filters.db");
        var store = new SqliteWorkflowStore(new ZhinuSqliteOptions
        {
            DatabasePath = database,
            BusyTimeout = TimeSpan.FromSeconds(2),
            Pooling = false
        });
        var registry = new WorkflowRegistry()
            .Register("alpha", "1", new NoopWorkflow())
            .Register("beta", "2", new NoopWorkflow());
        await using var engine = new WorkflowEngine(
            store,
            registry,
            new ZhinuOptions { PollInterval = TimeSpan.FromMilliseconds(10) });
        var alpha = await engine.StartAsync("alpha", "1", "x", cancellationToken: ct);
        var beta = await engine.StartAsync("beta", "2", "x", cancellationToken: ct);
        await engine.ExecuteAsync(alpha, ct);
        await engine.ExecuteAsync(beta, ct);

        var alphaOnly = await RunCliAsync("runs", "list", "--workflow", "alpha");
        alphaOnly.Should().Contain(alpha.ToString("D")).And.NotContain(beta.ToString("D"));

        var versionTwo = await RunCliAsync("runs", "list", "--version", "2");
        versionTwo.Should().Contain(beta.ToString("D")).And.NotContain(alpha.ToString("D"));

        var combined = await RunCliAsync("runs", "list", "--workflow", "alpha", "--version", "1");
        combined.Should().Contain(alpha.ToString("D")).And.NotContain(beta.ToString("D"));

        var missing = await RunCliAsync("runs", "list", "--workflow", "gamma");
        missing.Should().NotContain(alpha.ToString("D")).And.NotContain(beta.ToString("D"));

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

    private sealed class NoopWorkflow : IWorkflow<string, string>
    {
        public Task<string> RunAsync(
            WorkflowContext context, string input, CancellationToken cancellationToken) =>
            Task.FromResult(input);
    }
}
