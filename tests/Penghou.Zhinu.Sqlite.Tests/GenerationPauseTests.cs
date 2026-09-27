using FluentAssertions;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;

namespace Penghou.Zhinu.Sqlite.Tests;

/// <summary>
/// Pause, quiescence, and resume-before-cutover: an active generation pauses
/// to quiescing while retaining progression ownership, resumes before cutover,
/// and must be quiesced before a successor can supersede it at cutover.
/// </summary>
public sealed class GenerationPauseTests : WorkflowEngineTestBase
{
    [Fact]
    public async Task Pause_ActiveGeneration_RetainsOwnershipWithoutNewWork()
    {
        var ct = TestContext.Current.CancellationToken;
        var repository = Store();
        var runId = await StartRunAsync(ct);
        var instance = await repository.CreateInstanceAsync(null, ct);
        var first = await ActivateFirstAsync(repository, instance.InstanceId, runId, ct);

        var paused = await repository.PauseGenerationAsync(first.GenerationId, ct);

        paused.Status.Should().Be(WorkflowGenerationStatus.Quiescing);
        (await repository.GetActiveGenerationAsync(instance.InstanceId, ct))!
            .GenerationId.Should().Be(first.GenerationId);
        var again = () => repository.PauseGenerationAsync(first.GenerationId, ct).AsTask();
        await again.Should().ThrowAsync<WorkflowStateException>();
    }

    [Fact]
    public async Task Pause_NonActiveGeneration_Fails()
    {
        var ct = TestContext.Current.CancellationToken;
        var repository = Store();
        var runId = await StartRunAsync(ct);
        var instance = await repository.CreateInstanceAsync(null, ct);
        var created = await repository.CreateGenerationAsync(
            instance.InstanceId, runId, "plan-1", "fp-1", null, ct);

        var pauseCreated = () => repository.PauseGenerationAsync(created.GenerationId, ct).AsTask();
        await pauseCreated.Should().ThrowAsync<WorkflowStateException>();

        await repository.PrepareGenerationAsync(created.GenerationId, ct);
        var pausePrepared = () => repository.PauseGenerationAsync(created.GenerationId, ct).AsTask();
        await pausePrepared.Should().ThrowAsync<WorkflowStateException>();
    }

    [Fact]
    public async Task Resume_QuiescingGeneration_RestoresActive()
    {
        var ct = TestContext.Current.CancellationToken;
        var repository = Store();
        var runId = await StartRunAsync(ct);
        var instance = await repository.CreateInstanceAsync(null, ct);
        var first = await ActivateFirstAsync(repository, instance.InstanceId, runId, ct);
        await repository.PauseGenerationAsync(first.GenerationId, ct);

        var resumed = await repository.ResumeGenerationAsync(first.GenerationId, ct);

        resumed.Status.Should().Be(WorkflowGenerationStatus.Active);
        (await repository.GetActiveGenerationAsync(instance.InstanceId, ct))!
            .GenerationId.Should().Be(first.GenerationId);
    }

    [Fact]
    public async Task Resume_NonQuiescingGeneration_Fails()
    {
        var ct = TestContext.Current.CancellationToken;
        var repository = Store();
        var runId = await StartRunAsync(ct);
        var instance = await repository.CreateInstanceAsync(null, ct);
        var first = await ActivateFirstAsync(repository, instance.InstanceId, runId, ct);

        var resumeActive = () => repository.ResumeGenerationAsync(first.GenerationId, ct).AsTask();
        await resumeActive.Should().ThrowAsync<WorkflowStateException>();

        await repository.PauseGenerationAsync(first.GenerationId, ct);
        var second = await repository.CreateGenerationAsync(
            instance.InstanceId, runId, "plan-2", "fp-2", first.GenerationId, ct);
        await repository.PrepareGenerationAsync(second.GenerationId, ct);
        await repository.ActivateGenerationAsync(second.GenerationId, first.GenerationId, ct);

        var resumeSuperseded = () => repository.ResumeGenerationAsync(
            first.GenerationId, ct).AsTask();
        await resumeSuperseded.Should().ThrowAsync<WorkflowStateException>();
        (await repository.GetGenerationAsync(first.GenerationId, ct))!
            .Status.Should().Be(WorkflowGenerationStatus.Superseded);
    }

    [Fact]
    public async Task Cutover_WithoutPause_FailsWithoutSideEffects()
    {
        var ct = TestContext.Current.CancellationToken;
        var repository = Store();
        var runId = await StartRunAsync(ct);
        var instance = await repository.CreateInstanceAsync(null, ct);
        var first = await ActivateFirstAsync(repository, instance.InstanceId, runId, ct);
        var second = await repository.CreateGenerationAsync(
            instance.InstanceId, runId, "plan-2", "fp-2", first.GenerationId, ct);
        await repository.PrepareGenerationAsync(second.GenerationId, ct);

        var act = () => repository.ActivateGenerationAsync(
            second.GenerationId, first.GenerationId, ct).AsTask();

        await act.Should().ThrowAsync<WorkflowStateException>();
        (await repository.GetGenerationAsync(first.GenerationId, ct))!
            .Status.Should().Be(WorkflowGenerationStatus.Active);
        (await repository.GetGenerationAsync(second.GenerationId, ct))!
            .Status.Should().Be(WorkflowGenerationStatus.Prepared);
    }

    [Fact]
    public async Task RejectCandidate_ThenResume_BeforeCutover()
    {
        var ct = TestContext.Current.CancellationToken;
        var repository = Store();
        var runId = await StartRunAsync(ct);
        var instance = await repository.CreateInstanceAsync(null, ct);
        var first = await ActivateFirstAsync(repository, instance.InstanceId, runId, ct);
        await repository.PauseGenerationAsync(first.GenerationId, ct);
        var candidate = await repository.CreateGenerationAsync(
            instance.InstanceId, runId, "plan-2", "fp-2", first.GenerationId, ct);
        await repository.PrepareGenerationAsync(candidate.GenerationId, ct);

        await repository.RejectGenerationAsync(candidate.GenerationId, ct);
        var resumed = await repository.ResumeGenerationAsync(first.GenerationId, ct);

        resumed.Status.Should().Be(WorkflowGenerationStatus.Active);
        (await repository.GetGenerationAsync(candidate.GenerationId, ct))!
            .Status.Should().Be(WorkflowGenerationStatus.Rejected);
        (await repository.ListGenerationsAsync(instance.InstanceId, ct))
            .Select(item => (item.Ordinal, item.Status))
            .Should().Equal(
                (1, WorkflowGenerationStatus.Active),
                (2, WorkflowGenerationStatus.Rejected));
    }

    [Fact]
    public async Task Cutover_AfterPause_SupersedesAtomically()
    {
        var ct = TestContext.Current.CancellationToken;
        var repository = Store();
        var runId = await StartRunAsync(ct);
        var instance = await repository.CreateInstanceAsync(null, ct);
        var first = await ActivateFirstAsync(repository, instance.InstanceId, runId, ct);
        await repository.PauseGenerationAsync(first.GenerationId, ct);
        var second = await repository.CreateGenerationAsync(
            instance.InstanceId, runId, "plan-2", "fp-2", first.GenerationId, ct);
        await repository.PrepareGenerationAsync(second.GenerationId, ct);

        var active = await repository.ActivateGenerationAsync(
            second.GenerationId, first.GenerationId, ct);

        active.Status.Should().Be(WorkflowGenerationStatus.Active);
        active.ActivatedAt.Should().NotBeNull();
        var superseded = (await repository.GetGenerationAsync(first.GenerationId, ct))!;
        superseded.Status.Should().Be(WorkflowGenerationStatus.Superseded);
        superseded.SupersededAt.Should().NotBeNull();
        superseded.PredecessorGenerationId.Should().BeNull();
        active.PredecessorGenerationId.Should().Be(first.GenerationId);
    }

    [Fact]
    public async Task MissingEntities_FailWithNotFound()
    {
        var ct = TestContext.Current.CancellationToken;
        var repository = Store();

        await repository.Invoking(value => value.PauseGenerationAsync(
                Guid.NewGuid(), ct).AsTask())
            .Should().ThrowAsync<WorkflowNotFoundException>();
        await repository.Invoking(value => value.ResumeGenerationAsync(
                Guid.NewGuid(), ct).AsTask())
            .Should().ThrowAsync<WorkflowNotFoundException>();
    }

    private SqliteWorkflowStore Store() =>
        new(new SqliteDatabase(new ZhinuSqliteOptions
        {
            DatabasePath = Path.Combine(root, "zhinu.db"),
            BusyTimeout = TimeSpan.FromSeconds(2),
            Pooling = false
        }));

    private async Task<Guid> StartRunAsync(CancellationToken ct)
    {
        var name = $"gen-pause-{Guid.NewGuid():N}";
        var engine = CreateEngine(new SignalWorkflow(), name);
        return await engine.StartAsync(name, "1", "x", cancellationToken: ct);
    }

    private static async Task<WorkflowGeneration> ActivateFirstAsync(
        SqliteWorkflowStore repository,
        Guid instanceId,
        Guid runId,
        CancellationToken ct)
    {
        var created = await repository.CreateGenerationAsync(
            instanceId, runId, "plan-1", "fp-1", null, ct);
        var prepared = await repository.PrepareGenerationAsync(created.GenerationId, ct);
        return await repository.ActivateGenerationAsync(prepared.GenerationId, null, ct);
    }
}
