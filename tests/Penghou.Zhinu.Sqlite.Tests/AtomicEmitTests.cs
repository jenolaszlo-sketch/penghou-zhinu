using FluentAssertions;
#pragma warning disable xUnit1051

namespace Penghou.Zhinu.Sqlite.Tests;

public sealed class AtomicEmitTests : WorkflowEngineTestBase
{
    [Fact]
    public async Task Emit_InsideCompletingStep_IsCommittedWithTheStep()
    {
        var engine = CreateEngine(new EmitInsideStepWorkflow(fail: false), "atomic-emit-ok");
        var runId = await engine.StartAsync("atomic-emit-ok", "1", "x", cancellationToken: TestContext.Current.CancellationToken);
        await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
        var result = await engine.WaitForCompletionAsync<string>(runId, cancellationToken: TestContext.Current.CancellationToken);
        result.Should().Be("done");

        var events = await engine.GetEventsAsync(runId, cancellationToken: TestContext.Current.CancellationToken);
        events.Should().Contain(e => e.EventType == "my-progress");
        // The step's emitted event and the step completion are both present and ordered.
        var stepCompleted = events.First(e => e.EventType == WorkflowEventTypes.StepCompleted);
        var progress = events.First(e => e.EventType == "my-progress");
        progress.Sequence.Should().BeGreaterThan(stepCompleted.Sequence);
    }

    [Fact]
    public async Task Emit_InsideFailingStep_IsNotCommitted()
    {
        var engine = CreateEngine(new EmitInsideStepWorkflow(fail: true), "atomic-emit-fail");
        var runId = await engine.StartAsync("atomic-emit-fail", "1", "x", cancellationToken: TestContext.Current.CancellationToken);
        await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);

        var act = () => engine.WaitForCompletionAsync<string>(runId, cancellationToken: TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<WorkflowExecutionFailedException>();
        // The buffered emit was rolled back with the failed step: it must not appear.
        var events = await engine.GetEventsAsync(runId, cancellationToken: TestContext.Current.CancellationToken);
        events.Should().NotContain(e => e.EventType == "my-progress");
        events.Should().Contain(e => e.EventType == WorkflowEventTypes.StepFailed);
    }

    [Fact]
    public async Task Emit_InsideStep_ForwardsToPublisherAfterCommit()
    {
        var publisher = new RecordingPublisher();
        var engine = new WorkflowEngine(
            CreateStore(),
            new WorkflowRegistry().Register("atomic-pub", "1", new EmitInsideStepWorkflow(fail: false)),
            new ZhinuOptions { PollInterval = TimeSpan.FromMilliseconds(10) },
            eventPublisher: publisher);
        var runId = await engine.StartAsync("atomic-pub", "1", "x", cancellationToken: TestContext.Current.CancellationToken);
        await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
        await engine.WaitForCompletionAsync<string>(runId, cancellationToken: TestContext.Current.CancellationToken);

        publisher.Events.Should().Contain(e => e.EventType == "my-progress");
    }

    private sealed class EmitInsideStepWorkflow(bool fail) : IWorkflow<string, string>
    {
        public async Task<string> RunAsync(WorkflowContext ctx, string input, CancellationToken ct)
        {
            return await ctx.StepAsync<string, string>(
                "work",
                input,
                async (value, _, innerCt) =>
                {
                    await ctx.EmitAsync("my-progress", new { percent = 50 }, innerCt);
                    if (fail)
                        throw new InvalidOperationException("boom");
                    return "done";
                },
                cancellationToken: ct);
        }
    }
}
