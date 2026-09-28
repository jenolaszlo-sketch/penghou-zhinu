using FluentAssertions;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;

namespace Penghou.Zhinu.Sqlite.Tests;

/// <summary>
/// Application capability surfaces cover routine operations without
/// downcasting to the concrete engine: typed starts, waiting, progress
/// queries, interventions, and administration.
/// </summary>
public sealed class CapabilitySurfaceTests : WorkflowEngineTestBase
{
    [Fact]
    public async Task RoutineOperations_UseCapabilitiesOnly()
    {
        var ct = TestContext.Current.CancellationToken;
        var engine = CreateEngine(new TwoStepWorkflow(), "capabilities");
        IWorkflowStarter starter = engine;
        IWorkflowRuntime runtime = engine;
        IWorkflowClient client = engine;
        IWorkflowReader reader = engine;
        IWorkflowOperator operations = engine;
        IWorkflowAdministration administration = engine;

        var handle = await starter.StartHandleAsync<string, string>(
            "capabilities", "1", "x", cancellationToken: ct);
        await runtime.ExecuteAsync(handle.WorkflowRunId, ct);
        var result = await handle.WaitAsync(cancellationToken: ct);
        result.Should().Be("Hello, x!");

        var progress = await reader.GetRunProgressAsync(
            handle.WorkflowRunId, cancellationToken: ct);
        progress.Should().NotBeNull();
        (await reader.GetStepsAsync(handle.WorkflowRunId, ct)).Should().HaveCount(2);

        var plan = await operations.PlanRestartAsync(
            handle.WorkflowRunId, "first", cancellationToken: ct);
        plan.StepsToInvalidate.Select(item => item.StepKey)
            .Should().Contain("first");
        await operations.RestartStepAsync(
            handle.WorkflowRunId, "first", cancellationToken: ct);
        await runtime.ExecuteAsync(handle.WorkflowRunId, ct);
        (await client.WaitForCompletionAsync<string>(
            handle.WorkflowRunId, cancellationToken: ct)).Should().Be("Hello, x!");

        var pending = await starter.StartAsync(
            "capabilities", "1", "y", cancellationToken: ct);
        await administration.CancelAsync(pending, null, null, cancellationToken: ct);
        (await reader.GetRunAsync(pending, ct))!.Status.Should().Be(WorkflowStatus.Cancelled);

        var bulk = await operations.CancelManyAsync(
            new RunQuery { WorkflowName = "capabilities" }, cancellationToken: ct);
        bulk.Failed.Should().BeEmpty();
    }
}
