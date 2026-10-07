using FluentAssertions;
using Penghou.Zhinu;
using Penghou.Zhinu.Cli;
using Penghou.Zhinu.Sqlite;

namespace Penghou.Zhinu.Sqlite.Tests;

/// <summary>
/// Correlated diagnosis walkthrough: one command assembles run status,
/// failed steps, waits, external operations, and cancellation/restart audit
/// from the existing query surfaces, with payloads redacted by default.
/// </summary>
public sealed class RunEvidenceCliTests : WorkflowEngineTestBase
{
    [Fact]
    public async Task Walkthrough_CorrelatedRunEvidence()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = Path.Combine(root, "evidence.db");
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

        var requested = await store.RegisterAsync(new ExternalOperationRegistration
        {
            WorkflowRunId = runId,
            StepKey = "work",
            Attempt = 1,
            IdempotencyKey = "evidence-requested",
            Provider = "test-provider",
            RecoveryIntent = ExternalOperationRecoveryIntent.Retry,
            PayloadJson = "{\"profile\":\"sandbox\"}"
        }, ct);
        var cancelled = await store.RegisterAsync(new ExternalOperationRegistration
        {
            WorkflowRunId = runId,
            IdempotencyKey = "evidence-cancelled",
            Provider = "test-provider",
            RecoveryIntent = ExternalOperationRecoveryIntent.Abandon
        }, ct);
        await store.AcquireAsync(cancelled.OperationId, "cli-tester", cancelled.LeaseGeneration, ct);
        await store.CancelAsync(cancelled.OperationId, "AuthorityRevoked", ct);

        var evidence = await RunCliAsync("runs", "evidence", runId.ToString("D"));
        evidence.Code.Should().Be(0);
        evidence.Output.Should().Contain($"run: {runId:D}");
        evidence.Output.Should().Contain("Failed");
        evidence.Output.Should().Contain("work").And.Contain("boom");
        evidence.Output.Should().Contain(requested.OperationId.ToString("D"));
        evidence.Output.Should().Contain(cancelled.OperationId.ToString("D"));
        evidence.Output.Should().Contain("step-failed").And.Contain("workflow-failed");
        evidence.Output.Should().Contain("hidden");

        var open = await RunCliAsync(
            "runs", "evidence", runId.ToString("D"), "--include-payloads");
        open.Code.Should().Be(0);
        open.Output.Should().Contain("sandbox");

        await engine.RestartStepAsync(runId, "work", new RestartStepOptions(), ct);
        var pending = await RunCliAsync("runs", "evidence", runId.ToString("D"));
        pending.Code.Should().Be(0);
        pending.Output.Should().Contain("Pending");
        pending.Output.Should().Contain("step-restarted");

        var json = await RunCliAsync("--format", "json", "runs", "evidence", runId.ToString("D"));
        json.Code.Should().Be(0);
        json.Output.Should().Contain("\"Status\"");

        var missingRun = Guid.NewGuid();
        var missing = await RunCliAsync("runs", "evidence", missingRun.ToString("D"));
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
