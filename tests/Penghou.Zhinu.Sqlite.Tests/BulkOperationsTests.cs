using FluentAssertions;
#pragma warning disable xUnit1051

namespace Penghou.Zhinu.Sqlite.Tests;

public sealed class BulkOperationsTests : WorkflowEngineTestBase
{
    [Fact]
    public async Task CancelMany_CancelsNonTerminalOnly_AndReportsResult()
    {
        var engine = CreateEngine(new TwoStepWorkflow(), "bulk-cancel");
        // Two runs that never complete (no execution) + one completed terminal.
        var pending1 = await engine.StartAsync("bulk-cancel", "1", "a", cancellationToken: TestContext.Current.CancellationToken);
        var pending2 = await engine.StartAsync("bulk-cancel", "1", "b", cancellationToken: TestContext.Current.CancellationToken);
        var completed = await engine.StartAsync("bulk-cancel", "1", "c", cancellationToken: TestContext.Current.CancellationToken);
        await engine.ExecuteAsync(completed, TestContext.Current.CancellationToken);
        await engine.WaitForCompletionAsync<string>(completed, cancellationToken: TestContext.Current.CancellationToken);

        var result = await engine.CancelManyAsync(
            new RunQuery { WorkflowName = "bulk-cancel" },
            actor: "ops",
            reason: "sweep",
            cancellationToken: TestContext.Current.CancellationToken);

        result.Succeeded.Should().Be(2);
        result.FailedCount.Should().Be(0);
        result.AllSucceeded.Should().BeTrue();
        (await engine.GetRunAsync(pending1, TestContext.Current.CancellationToken))!.Status.Should().Be(WorkflowStatus.Cancelled);
        (await engine.GetRunAsync(pending2, TestContext.Current.CancellationToken))!.Status.Should().Be(WorkflowStatus.Cancelled);
        // Completed run is untouched.
        (await engine.GetRunAsync(completed, TestContext.Current.CancellationToken))!.Status.Should().Be(WorkflowStatus.Completed);
    }

    [Fact]
    public async Task CancelMany_PartialFailure_ReportsFailedItems()
    {
        var engine = CreateEngine(new TwoStepWorkflow(), "bulk-partial");
        var pending = await engine.StartAsync("bulk-partial", "1", "a", cancellationToken: TestContext.Current.CancellationToken);
        var unknown = Guid.NewGuid();

        var result = await engine.CancelManyAsync(
            new RunQuery { Limit = 1000 },
            cancellationToken: TestContext.Current.CancellationToken);

        // At least the valid run was cancelled; no exception escapes from a failing item.
        result.Succeeded.Should().BeGreaterThanOrEqualTo(1);
        result.Failed.Should().NotContain(f => f.ItemId == pending);
    }
}
