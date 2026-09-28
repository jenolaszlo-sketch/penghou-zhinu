using FluentAssertions;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;

namespace Penghou.Zhinu.Sqlite.Tests;

/// <summary>
/// Stale-worker fencing for signal delivery: a superseded revision, a stale
/// caller generation, or a live rival lease must fail closed with
/// <see cref="LeaseLostException"/> instead of consuming a signal into
/// obsolete work. Each test drives a real wait workflow, then delivers
/// through a peer store sharing the database file.
/// </summary>
public sealed class StaleWorkerSignalFencingTests : WorkflowEngineTestBase
{
    [Fact]
    public async Task Restart_ReplacesRevision_DeliveryToOldRevisionRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var engine = CreateEngine(new SignalWorkflow(), "fence-restart");
        var runId = await engine.StartAsync(
            "fence-restart", "1", "x", cancellationToken: ct);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var execution = engine.ExecuteAsync(runId, cts.Token);
        await WaitUntilAsync(
            () => HasStepStatusAsync(engine, runId, "approval", StepStatus.Waiting, cts.Token),
            cts.Token);
        var oldStep = (await engine.GetStepsAsync(runId, cts.Token))
            .Single(item => item.StepKey == "approval");

        await engine.RestartStepAsync(runId, "approval", cts.Token);
        var current = (await engine.GetStepsAsync(runId, cts.Token))
            .Where(item => item.StepKey == "approval")
            .OrderByDescending(item => item.Revision)
            .First();
        current.Revision.Should().Be(oldStep.Revision + 1);

        var peer = PeerStore();
        var act = () => peer.TryDeliverSignalAsync(
            oldStep.Id, "stale-worker", current.LeaseGeneration, "approve",
            DateTimeOffset.UtcNow, ct).AsTask();

        (await act.Should().ThrowAsync<LeaseLostException>())
            .WithMessage("*superseded*");
        await cts.CancelAsync();
        try { await execution; } catch { }
    }

    [Fact]
    public async Task RivalOwner_WithLiveLease_DeliveryRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var workflow = new GateWorkflow();
        var engine = CreateEngine(workflow, "fence-rival", TimeSpan.FromMinutes(5));
        var runId = await engine.StartAsync(
            "fence-rival", "1", "x", cancellationToken: ct);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var execution = engine.ExecuteAsync(runId, cts.Token);
        await WaitUntilAsync(() => HasStepStatusAsync(
            engine, runId, "gate", StepStatus.Running, cts.Token), cts.Token);
        var step = (await engine.GetStepsAsync(runId, cts.Token))
            .Single(item => item.StepKey == "gate");

        var peer = PeerStore();
        var act = () => peer.TryDeliverSignalAsync(
            step.Id, "rival-worker", step.LeaseGeneration, "approve",
            DateTimeOffset.UtcNow, ct).AsTask();

        (await act.Should().ThrowAsync<LeaseLostException>())
            .WithMessage("*another worker*");
        workflow.Release();
        await execution;
    }

    [Fact]
    public async Task StaleCallerGeneration_DeliveryRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var engine = CreateEngine(new SignalWorkflow(), "fence-generation");
        var runId = await engine.StartAsync(
            "fence-generation", "1", "x", cancellationToken: ct);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var execution = engine.ExecuteAsync(runId, cts.Token);
        await WaitUntilAsync(
            () => HasStepStatusAsync(engine, runId, "approval", StepStatus.Waiting, cts.Token),
            cts.Token);
        await engine.RestartStepAsync(runId, "approval", cts.Token);
        await cts.CancelAsync();
        try { await execution; } catch { }

        using var cts2 = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var redrive = engine.ExecuteAsync(runId, cts2.Token);
        WorkflowStepRun? current = null;
        await WaitUntilAsync(
            async () =>
            {
                current = (await engine.GetStepsAsync(runId, cts2.Token))
                    .Where(item => item.StepKey == "approval")
                    .OrderByDescending(item => item.Revision)
                    .First();
                return current.Status == StepStatus.Waiting;
            },
            cts2.Token);

        var peer = PeerStore();
        var act = () => peer.TryDeliverSignalAsync(
            current!.Id, "stale-worker", current.LeaseGeneration - 1, "approve",
            DateTimeOffset.UtcNow, ct).AsTask();

        (await act.Should().ThrowAsync<LeaseLostException>())
            .WithMessage("*generation*");
        await cts2.CancelAsync();
        try { await redrive; } catch { }
    }

    [Fact]
    public async Task LateSignal_AfterCompletion_ReturnsExistingOutput()
    {
        var ct = TestContext.Current.CancellationToken;
        var engine = CreateEngine(new SignalWorkflow(), "fence-late");
        var runId = await engine.StartAsync(
            "fence-late", "1", "x", cancellationToken: ct);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var execution = engine.ExecuteAsync(runId, cts.Token);
        await WaitUntilAsync(
            () => HasStepStatusAsync(engine, runId, "approval", StepStatus.Waiting, cts.Token),
            cts.Token);
        await engine.SendSignalAsync(runId, "approve", "yes", cts.Token);
        await execution;
        await engine.ExecuteAsync(runId, cts.Token);
        var completed = (await engine.GetStepsAsync(runId, ct))
            .Single(item => item.StepKey == "approval");
        completed.Status.Should().Be(StepStatus.Completed);

        await engine.SendSignalAsync(runId, "approve", "late", cts.Token);
        var peer = PeerStore();
        var delivery = await peer.TryDeliverSignalAsync(
            completed.Id, "any-owner", completed.LeaseGeneration, "approve",
            DateTimeOffset.UtcNow, ct);

        delivery.Should().NotBeNull();
        delivery!.Value.DataJson.Should().Contain("yes");
    }

    private SqliteWorkflowStore PeerStore() =>
        new(new ZhinuSqliteOptions
        {
            DatabasePath = Path.Combine(root, "zhinu.db"),
            BusyTimeout = TimeSpan.FromSeconds(2),
            Pooling = false
        });

    private sealed class GateWorkflow : IWorkflow<string, string>
    {
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _release.TrySetResult();

        public Task<string> RunAsync(
            WorkflowContext context, string input, CancellationToken cancellationToken) =>
            context.StepAsync(
                "gate",
                async stepCt =>
                {
                    await _release.Task.WaitAsync(stepCt).ConfigureAwait(false);
                    return input;
                },
                cancellationToken: cancellationToken);
    }
}

