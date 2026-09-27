using FluentAssertions;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;

namespace Penghou.Zhinu.Sqlite.Tests;

/// <summary>
/// Generation-aware scheduling guard: bound runs schedule no new work while
/// quiescing (claims defer and resume) and stop scheduling once superseded,
/// while unbound legacy runs keep historical behavior.
/// </summary>
public sealed class GenerationSchedulingGuardTests : WorkflowEngineTestBase
{
    [Fact]
    public async Task QuiescingRun_DefersClaimsUntilResumed()
    {
        var ct = TestContext.Current.CancellationToken;
        var engine = CreateEngine(new SignalWorkflow(), "claim-guard-pause");
        var store = PeerStore();
        var runId = await engine.StartAsync("claim-guard-pause", "1", "x", cancellationToken: ct);
        var generation = (await store.GetGenerationByRunAsync(runId, ct))!;

        (await ClaimAsync(store, runId, "s1", ct)).Disposition
            .Should().Be(StepClaimDisposition.Acquired);
        await store.PauseGenerationAsync(generation.GenerationId, ct);

        (await ClaimAsync(store, runId, "s2", ct)).Disposition
            .Should().Be(StepClaimDisposition.Deferred);
        await store.ResumeGenerationAsync(generation.GenerationId, ct);
        (await ClaimAsync(store, runId, "s3", ct)).Disposition
            .Should().Be(StepClaimDisposition.Acquired);
    }

    [Fact]
    public async Task SupersededRun_RefusesClaims()
    {
        var ct = TestContext.Current.CancellationToken;
        var engine = CreateEngine(new SignalWorkflow(), "claim-guard-cutover");
        var store = PeerStore();
        var runId = await engine.StartAsync(
            "claim-guard-cutover", "1", "x", cancellationToken: ct);
        var first = (await store.GetGenerationByRunAsync(runId, ct))!;
        var run2 = await engine.StartAsync(
            "claim-guard-cutover", "1", "y", cancellationToken: ct);
        var candidate = await store.CreateGenerationAsync(
            first.InstanceId, run2, "plan-2", "fp-2", first.GenerationId, ct);
        await store.PrepareGenerationAsync(candidate.GenerationId, ct);
        await store.PauseGenerationAsync(first.GenerationId, ct);
        await store.ActivateGenerationAsync(candidate.GenerationId, first.GenerationId, ct);

        (await ClaimAsync(store, runId, "s1", ct)).Disposition
            .Should().Be(StepClaimDisposition.Superseded);
        // The successor generation's run still schedules.
        (await ClaimAsync(store, run2, "s1", ct)).Disposition
            .Should().Be(StepClaimDisposition.Acquired);
    }

    [Fact]
    public async Task UnboundRun_KeepsHistoricalClaimBehavior()
    {
        var ct = TestContext.Current.CancellationToken;
        var engine = CreateEngine(new SignalWorkflow(), "claim-guard-legacy");
        var store = PeerStore();
        var template = await engine.StartAsync(
            "claim-guard-legacy", "1", "x", cancellationToken: ct);
        var source = (await store.GetRunAsync(template, ct))!;
        var runId = Guid.NewGuid();
        await store.CreateRunAsync(source with { Id = runId }, ct);

        (await ClaimAsync(store, runId, "s1", ct)).Disposition
            .Should().Be(StepClaimDisposition.Acquired);
    }

    [Fact]
    public async Task InFlightAdmission_DoesNotFenceClaims()
    {
        var ct = TestContext.Current.CancellationToken;
        var engine = CreateEngine(new SignalWorkflow(), "claim-guard-admission");
        var store = PeerStore();
        var template = await engine.StartAsync(
            "claim-guard-admission", "1", "x", cancellationToken: ct);
        var source = (await store.GetRunAsync(template, ct))!;
        var runId = Guid.NewGuid();
        await store.CreateRunAsync(source with { Id = runId }, ct);
        var instance = await store.CreateInstanceAsync(null, ct);
        await store.CreateGenerationAsync(instance.InstanceId, runId, "plan-1", "fp-1", null, ct);

        (await ClaimAsync(store, runId, "s1", ct)).Disposition
            .Should().Be(StepClaimDisposition.Acquired);
    }

    [Fact]
    public async Task PausedExecution_ResumesAndCompletes()
    {
        var ct = TestContext.Current.CancellationToken;
        var workflow = new TwoStepWorkflow();
        var engine = CreateEngine(workflow, "claim-guard-resume");
        var store = PeerStore();
        var runId = await engine.StartAsync(
            "claim-guard-resume", "1", "x", cancellationToken: ct);
        var generation = (await store.GetGenerationByRunAsync(runId, ct))!;
        await store.PauseGenerationAsync(generation.GenerationId, ct);

        var executing = engine.ExecuteAsync(runId, ct);
        await Task.Delay(TimeSpan.FromMilliseconds(750), ct);
        await store.ResumeGenerationAsync(generation.GenerationId, ct);
        var result = await engine.WaitForCompletionAsync<string>(runId, cancellationToken: ct);
        await executing;

        result.Should().Be("Hello, x!");
    }

    [Fact]
    public async Task SupersededExecution_StopsScheduling()
    {
        var ct = TestContext.Current.CancellationToken;
        var engine = CreateEngine(new TwoStepWorkflow(), "claim-guard-abort");
        var store = PeerStore();
        var runId = await engine.StartAsync(
            "claim-guard-abort", "1", "x", cancellationToken: ct);
        var first = (await store.GetGenerationByRunAsync(runId, ct))!;
        var run2 = await engine.StartAsync(
            "claim-guard-abort", "1", "y", cancellationToken: ct);
        var candidate = await store.CreateGenerationAsync(
            first.InstanceId, run2, "plan-2", "fp-2", first.GenerationId, ct);
        await store.PrepareGenerationAsync(candidate.GenerationId, ct);
        await store.PauseGenerationAsync(first.GenerationId, ct);
        await store.ActivateGenerationAsync(candidate.GenerationId, first.GenerationId, ct);

        var act = () => engine.ExecuteAsync(runId, ct);

        await act.Should().NotThrowAsync();
        var run = (await store.GetRunAsync(runId, ct))!;
        run.Status.Should().Be(WorkflowStatus.Failed);
        run.Error.Should().NotBeNull();
        run.Error!.Message.Should().Contain("no longer owns forward progression");
    }

    private static async Task<StepClaimResult> ClaimAsync(
        SqliteWorkflowStore store, Guid runId, string stepKey, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        return await store.ClaimStepAsync(
            new StepClaimRequest
            {
                WorkflowRunId = runId,
                StepKey = stepKey,
                OutputType = "string",
                OwnerId = "worker-1",
                Now = now,
                LeaseExpiresAt = now.AddMinutes(5)
            },
            ct);
    }

    [Fact]
    public async Task PausedGeneration_DoesNotExecuteRetry()
    {
        var workflow = new RetryWhilePausedWorkflow();
        var store = PeerStore();
        await using var engine = new WorkflowEngine(
            store,
            new WorkflowRegistry().Register("retry", "1", workflow),
            new ZhinuOptions { PollInterval = TimeSpan.FromMilliseconds(10) });
        var id = await engine.StartAsync("retry", "1", "x", cancellationToken: TestContext.Current.CancellationToken);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var execution = engine.ExecuteAsync(id, stop.Token);
        try
        {
            while (!(await engine.GetStepsAsync(id, stop.Token)).Any(s => s.Status == StepStatus.Waiting))
                await Task.Delay(10, stop.Token);
            var generation = (await store.GetGenerationByRunAsync(id, stop.Token))!;
            await store.PauseGenerationAsync(generation.GenerationId, stop.Token);
            await Task.Delay(1000, stop.Token);
            workflow.Calls.Should().Be(1, "a paused generation must not start a second attempt");
        }
        finally
        {
            await stop.CancelAsync();
            await execution;
        }
    }

    private sealed class RetryWhilePausedWorkflow : IWorkflow<string, string>
    {
        public int Calls;

        public Task<string> RunAsync(
            WorkflowContext context, string input, CancellationToken cancellationToken) =>
            context.StepAsync(
                "retry",
                input,
                async (_, ct) =>
                {
                    if (Interlocked.Increment(ref Calls) == 1)
                        throw new InvalidOperationException("transient");
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                    return input;
                },
                new StepOptions { Retry = new RetryPolicy { MaxAttempts = 2, InitialDelay = TimeSpan.FromMilliseconds(400) } },
                cancellationToken);
    }

    [Fact]
    public async Task PausedRetry_ResumesWithFreshClaimAndCommits()
    {
        var workflow = new FlakyWorkflow();
        var store = PeerStore();
        await using var engine = new WorkflowEngine(
            store,
            new WorkflowRegistry().Register("retry-resume", "1", workflow),
            new ZhinuOptions { PollInterval = TimeSpan.FromMilliseconds(10) });
        var id = await engine.StartAsync(
            "retry-resume", "1", "x", cancellationToken: TestContext.Current.CancellationToken);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var execution = engine.ExecuteAsync(id, stop.Token);
        try
        {
            while (!(await engine.GetStepsAsync(id, stop.Token)).Any(s => s.Status == StepStatus.Waiting))
                await Task.Delay(10, stop.Token);
            var generation = (await store.GetGenerationByRunAsync(id, stop.Token))!;
            await store.PauseGenerationAsync(generation.GenerationId, stop.Token);
            await Task.Delay(750, stop.Token);
            workflow.Calls.Should().Be(1, "no second attempt may start while paused");
            await store.ResumeGenerationAsync(generation.GenerationId, stop.Token);
            var result = await engine.WaitForCompletionAsync<string>(id, cancellationToken: stop.Token);
            await execution;
            result.Should().Be("recovered:x");
            workflow.Calls.Should().Be(2, "exactly one additional attempt starts after resume");
        }
        finally
        {
            await stop.CancelAsync();
            await execution;
        }
    }

    [Fact]
    public async Task SupersedeWhileDeferred_StopsWithoutNewInvocation()
    {
        var workflow = new FlakyWorkflow();
        var store = PeerStore();
        await using var engine = new WorkflowEngine(
            store,
            new WorkflowRegistry().Register("retry-supersede", "1", workflow),
            new ZhinuOptions { PollInterval = TimeSpan.FromMilliseconds(10) });
        var id = await engine.StartAsync(
            "retry-supersede", "1", "x", cancellationToken: TestContext.Current.CancellationToken);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var execution = engine.ExecuteAsync(id, stop.Token);
        try
        {
            while (!(await engine.GetStepsAsync(id, stop.Token)).Any(s => s.Status == StepStatus.Waiting))
                await Task.Delay(10, stop.Token);
            var first = (await store.GetGenerationByRunAsync(id, stop.Token))!;
            var run2 = await engine.StartAsync(
                "retry-supersede", "1", "y", cancellationToken: stop.Token);
            var candidate = await store.CreateGenerationAsync(
                first.InstanceId, run2, "plan-2", "fp-2", first.GenerationId, stop.Token);
            await store.PrepareGenerationAsync(candidate.GenerationId, stop.Token);
            await store.PauseGenerationAsync(first.GenerationId, stop.Token);
            await store.ActivateGenerationAsync(candidate.GenerationId, first.GenerationId, stop.Token);
            await execution;
            workflow.Calls.Should().Be(1, "no subsequent invocation from the old execution");
            (await store.GetRunAsync(id, stop.Token))!.Status.Should().Be(WorkflowStatus.Failed);
        }
        finally
        {
            await stop.CancelAsync();
            await execution;
        }
    }

    [Fact]
    public async Task CancelWhileDeferred_ExitsPromptly()
    {
        var workflow = new RetryWhilePausedWorkflow();
        var store = PeerStore();
        await using var engine = new WorkflowEngine(
            store,
            new WorkflowRegistry().Register("retry-cancel", "1", workflow),
            new ZhinuOptions { PollInterval = TimeSpan.FromMilliseconds(10) });
        var id = await engine.StartAsync(
            "retry-cancel", "1", "x", cancellationToken: TestContext.Current.CancellationToken);
        using var stop = new CancellationTokenSource();
        var execution = engine.ExecuteAsync(id, stop.Token);
        try
        {
            while (!(await engine.GetStepsAsync(id, stop.Token)).Any(s => s.Status == StepStatus.Waiting))
                await Task.Delay(10, stop.Token);
            var generation = (await store.GetGenerationByRunAsync(id, stop.Token))!;
            await store.PauseGenerationAsync(generation.GenerationId, stop.Token);
            await Task.Delay(500, stop.Token);
            await stop.CancelAsync();
            await execution;
        }
        finally
        {
            await stop.CancelAsync();
            try { await execution; } catch (OperationCanceledException) { }
        }
        workflow.Calls.Should().Be(1, "cancellation must not start new attempts");
    }

    private sealed class FlakyWorkflow : IWorkflow<string, string>
    {
        public int Calls;

        public Task<string> RunAsync(
            WorkflowContext context, string input, CancellationToken cancellationToken) =>
            context.StepAsync(
                "flaky",
                input,
                (_, _) =>
                {
                    if (Interlocked.Increment(ref Calls) == 1)
                        throw new InvalidOperationException("transient");
                    return Task.FromResult($"recovered:{input}");
                },
                new StepOptions { Retry = new RetryPolicy { MaxAttempts = 2, InitialDelay = TimeSpan.FromMilliseconds(400) } },
                cancellationToken);
    }

    private SqliteWorkflowStore PeerStore() =>
        new(new ZhinuSqliteOptions
        {
            DatabasePath = Path.Combine(root, "zhinu.db"),
            BusyTimeout = TimeSpan.FromSeconds(2),
            Pooling = false
        });
}
