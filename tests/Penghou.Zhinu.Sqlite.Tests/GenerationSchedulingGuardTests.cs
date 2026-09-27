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

    private SqliteWorkflowStore PeerStore() =>
        new(new ZhinuSqliteOptions
        {
            DatabasePath = Path.Combine(root, "zhinu.db"),
            BusyTimeout = TimeSpan.FromSeconds(2),
            Pooling = false
        });
}
