using FluentAssertions;
using Penghou.Zhinu;
using Penghou.Zhinu.Cli;
using Penghou.Zhinu.Sqlite;

namespace Penghou.Zhinu.Sqlite.Tests;

/// <summary>
/// Operator inspection of durable external-operation handles: list a run's
/// operations, filter by status, show one handle with redacted correlation
/// payloads, and fail honestly on unknown runs, operations, and statuses.
/// </summary>
public sealed class ExternalOperationCliTests : WorkflowEngineTestBase
{
    [Fact]
    public async Task Walkthrough_ListFilterShowExternalOperations()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = Path.Combine(root, "external-ops.db");
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

        var running = await store.RegisterAsync(new ExternalOperationRegistration
        {
            WorkflowRunId = runId,
            StepKey = "probe",
            StepRevision = 1,
            Attempt = 1,
            IdempotencyKey = "op-running",
            Provider = "test-provider",
            RecoveryIntent = ExternalOperationRecoveryIntent.Retry,
            PayloadJson = "{\"profile\":\"sandbox\"}"
        }, ct);
        await store.AcquireAsync(running.OperationId, "cli-tester", running.LeaseGeneration, ct);

        var completed = await store.RegisterAsync(new ExternalOperationRegistration
        {
            WorkflowRunId = runId,
            StepKey = "probe",
            Attempt = 1,
            IdempotencyKey = "op-completed",
            Provider = "test-provider",
            RecoveryIntent = ExternalOperationRecoveryIntent.Resume,
            PayloadJson = "{\"invocation\":\"whoami\"}"
        }, ct);
        await store.AcquireAsync(completed.OperationId, "cli-tester", completed.LeaseGeneration, ct);
        await store.CompleteAsync(completed.OperationId, "cli-tester", "{\"rootExitCode\":0}", ct);

        var failed = await store.RegisterAsync(new ExternalOperationRegistration
        {
            WorkflowRunId = runId,
            IdempotencyKey = "op-failed",
            Provider = "test-provider",
            RecoveryIntent = ExternalOperationRecoveryIntent.Abandon
        }, ct);
        await store.AcquireAsync(failed.OperationId, "cli-tester", failed.LeaseGeneration, ct);
        await store.FailAsync(failed.OperationId, "cli-tester", "RootExitCode=3", ct);

        var cancelled = await store.RegisterAsync(new ExternalOperationRegistration
        {
            WorkflowRunId = runId,
            IdempotencyKey = "op-cancelled",
            Provider = "test-provider",
            RecoveryIntent = ExternalOperationRecoveryIntent.Abandon
        }, ct);
        await store.CancelAsync(cancelled.OperationId, "AuthorityRevoked", ct);

        var requested = await store.RegisterAsync(new ExternalOperationRegistration
        {
            WorkflowRunId = runId,
            IdempotencyKey = "op-requested",
            Provider = "test-provider",
            RecoveryIntent = ExternalOperationRecoveryIntent.Retry
        }, ct);

        var (listCode, list) = await RunCliAsync("runs", "external-ops", "list", runId.ToString("D"));
        listCode.Should().Be(0);
        list.Should().Contain(running.OperationId.ToString("D"))
            .And.Contain("Running")
            .And.Contain("Cancelled")
            .And.Contain("test-provider");

        var (filteredCode, filtered) = await RunCliAsync(
            "runs", "external-ops", "list", runId.ToString("D"), "--status", "Running");
        filteredCode.Should().Be(0);
        filtered.Should().Contain(running.OperationId.ToString("D"));
        filtered.Should().NotContain(failed.OperationId.ToString("D"));

        var (cancelledCode, cancelledList) = await RunCliAsync(
            "runs", "external-ops", "list", runId.ToString("D"), "--status", "cancelled");
        cancelledCode.Should().Be(0);
        cancelledList.Should().Contain(cancelled.OperationId.ToString("D"));
        cancelledList.Should().NotContain(requested.OperationId.ToString("D"));

        var (showCode, show) = await RunCliAsync(
            "runs", "external-ops", "show", completed.OperationId.ToString("D"));
        showCode.Should().Be(0);
        show.Should().Contain("status: Completed").And.Contain("hidden");

        var (openCode, open) = await RunCliAsync(
            "runs", "external-ops", "show", completed.OperationId.ToString("D"), "--include-payloads");
        openCode.Should().Be(0);
        open.Should().Contain("rootExitCode");

        var (failedShowCode, failedShow) = await RunCliAsync(
            "runs", "external-ops", "show", failed.OperationId.ToString("D"));
        failedShowCode.Should().Be(0);
        failedShow.Should().Contain("RootExitCode=3");

        var (jsonCode, json) = await RunCliAsync(
            "--format", "json", "runs", "external-ops", "list", runId.ToString("D"), "--limit", "2");
        jsonCode.Should().Be(0);
        json.Should().Contain("\"Status\"");

        var (badStatusCode, badStatus) = await RunCliAsync(
            "runs", "external-ops", "list", runId.ToString("D"), "--status", "Bogus");
        badStatusCode.Should().Be(1);
        badStatus.Should().Contain("unknown operation status");

        var missingRun = Guid.NewGuid();
        var (missingRunCode, missingRunOut) = await RunCliAsync(
            "runs", "external-ops", "list", missingRun.ToString("D"));
        missingRunCode.Should().Be(2);
        missingRunOut.Should().Contain("not found");

        var missingOp = Guid.NewGuid();
        var (missingOpCode, missingOpOut) = await RunCliAsync(
            "runs", "external-ops", "show", missingOp.ToString("D"));
        missingOpCode.Should().Be(2);
        missingOpOut.Should().Contain("not found");

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
