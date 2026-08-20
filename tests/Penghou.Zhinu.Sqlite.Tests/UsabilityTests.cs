using FluentAssertions;
using Penghou.Zhinu.Testing;

namespace Penghou.Zhinu.Sqlite.Tests;

public sealed class UsabilityTests : WorkflowEngineTestBase
{
    [Fact]
    public async Task TypedHandle_ReturnsNonThrowingResult()
    {
        var engine = CreateEngine(new EchoWorkflow(), "echo");
        var handle = await engine.StartHandleAsync<string, string>(
            "echo", "1", "hello", cancellationToken: TestContext.Current.CancellationToken);

        (await handle.GetResultAsync(TestContext.Current.CancellationToken)).Status
            .Should().Be(WorkflowStatus.Pending);
        await engine.ExecuteAsync(handle.WorkflowRunId, TestContext.Current.CancellationToken);
        var result = await handle.GetResultAsync(TestContext.Current.CancellationToken);

        result.Status.Should().Be(WorkflowStatus.Completed);
        result.Value.Should().Be("hello");
        result.IsTerminal.Should().BeTrue();
    }

    [Fact]
    public async Task TestingPackage_StorePassesRoundTripConformance()
    {
        await WorkflowStoreConformance.VerifyRunRoundTripAsync(
            CreateStore(), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task DisposedEngine_RejectsNewRuns()
    {
        var engine = CreateEngine(new EchoWorkflow(), "echo");
        await engine.DisposeAsync();

        var action = () => engine.StartAsync(
            "echo", "1", "hello", cancellationToken: TestContext.Current.CancellationToken);
        await action.Should().ThrowAsync<ObjectDisposedException>();
    }

    private sealed class EchoWorkflow : IWorkflow<string, string>
    {
        public Task<string> RunAsync(
            WorkflowContext context, string input, CancellationToken cancellationToken) =>
            Task.FromResult(input);
    }
}
