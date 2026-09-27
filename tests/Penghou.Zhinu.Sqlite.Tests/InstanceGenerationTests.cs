using FluentAssertions;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;

namespace Penghou.Zhinu.Sqlite.Tests;

/// <summary>
/// Workflow-instance identity and atomic generation cutover: dense ordinals,
/// single active owner, supersession that never reactivates, rejection that
/// leaves the current generation resumable, and crash-safe resume at every
/// intermediate state.
/// </summary>
public sealed class InstanceGenerationTests : WorkflowEngineTestBase
{
    [Fact]
    public async Task FirstGeneration_ActivatesWithoutPredecessor()
    {
        var ct = TestContext.Current.CancellationToken;
        var repository = Store();
        var runId = await StartRunAsync(ct);
        var instance = await repository.CreateInstanceAsync(null, ct);
        var created = await repository.CreateGenerationAsync(
            instance.InstanceId, runId, "plan-1", "fp-1", predecessorGenerationId: null, ct);

        created.Ordinal.Should().Be(1);
        created.Status.Should().Be(WorkflowGenerationStatus.Created);
        (await repository.GetActiveGenerationAsync(instance.InstanceId, ct)).Should().BeNull();

        var prepared = await repository.PrepareGenerationAsync(created.GenerationId, ct);
        prepared.Status.Should().Be(WorkflowGenerationStatus.Prepared);
        var active = await repository.ActivateGenerationAsync(
            created.GenerationId, expectedPredecessorGenerationId: null, ct);

        active.Status.Should().Be(WorkflowGenerationStatus.Active);
        active.ActivatedAt.Should().NotBeNull();
        (await repository.GetActiveGenerationAsync(instance.InstanceId, ct))!
            .GenerationId.Should().Be(created.GenerationId);
    }

    [Fact]
    public async Task SecondGeneration_RequiresPredecessor_AndSupersedesAtomically()
    {
        var ct = TestContext.Current.CancellationToken;
        var repository = Store();
        var runId = await StartRunAsync(ct);
        var instance = await repository.CreateInstanceAsync(null, ct);
        var first = await ActivateAsync(repository, instance.InstanceId, runId, "plan-1", null, ct);

        var second = await repository.CreateGenerationAsync(
            instance.InstanceId, runId, "plan-2", "fp-2", first.GenerationId, ct);
        second.Ordinal.Should().Be(2);
        await repository.PrepareGenerationAsync(second.GenerationId, ct);
        await repository.PauseGenerationAsync(first.GenerationId, ct);
        var active = await repository.ActivateGenerationAsync(
            second.GenerationId, first.GenerationId, ct);

        active.Status.Should().Be(WorkflowGenerationStatus.Active);
        (await repository.GetGenerationAsync(first.GenerationId, ct))!
            .Status.Should().Be(WorkflowGenerationStatus.Superseded);
        (await repository.GetActiveGenerationAsync(instance.InstanceId, ct))!
            .GenerationId.Should().Be(second.GenerationId);
        (await repository.ListGenerationsAsync(instance.InstanceId, ct))
            .Select(item => (item.Ordinal, item.Status))
            .Should().Equal(
                (1, WorkflowGenerationStatus.Superseded),
                (2, WorkflowGenerationStatus.Active));
    }

    [Fact]
    public async Task Activation_WithWrongPredecessor_FailsWithoutSideEffects()
    {
        var ct = TestContext.Current.CancellationToken;
        var repository = Store();
        var runId = await StartRunAsync(ct);
        var instance = await repository.CreateInstanceAsync(null, ct);
        var first = await ActivateAsync(repository, instance.InstanceId, runId, "plan-1", null, ct);
        var second = await repository.CreateGenerationAsync(
            instance.InstanceId, runId, "plan-2", "fp-2", first.GenerationId, ct);
        await repository.PrepareGenerationAsync(second.GenerationId, ct);

        var act = () => repository.ActivateGenerationAsync(
            second.GenerationId, Guid.NewGuid(), ct).AsTask();

        await act.Should().ThrowAsync<WorkflowStateException>();
        (await repository.GetActiveGenerationAsync(instance.InstanceId, ct))!
            .GenerationId.Should().Be(first.GenerationId);
        (await repository.GetGenerationAsync(second.GenerationId, ct))!
            .Status.Should().Be(WorkflowGenerationStatus.Prepared);
    }

    [Fact]
    public async Task SecondActiveOwner_IsRejected()
    {
        var ct = TestContext.Current.CancellationToken;
        var repository = Store();
        var runId = await StartRunAsync(ct);
        var instance = await repository.CreateInstanceAsync(null, ct);
        var first = await ActivateAsync(repository, instance.InstanceId, runId, "plan-1", null, ct);
        var second = await repository.CreateGenerationAsync(
            instance.InstanceId, runId, "plan-2", "fp-2", first.GenerationId, ct);
        await repository.PrepareGenerationAsync(second.GenerationId, ct);
        var rival = await repository.CreateGenerationAsync(
            instance.InstanceId, runId, "plan-X", "fp-X", first.GenerationId, ct);
        await repository.PrepareGenerationAsync(rival.GenerationId, ct);

        await repository.PauseGenerationAsync(first.GenerationId, ct);
        await repository.ActivateGenerationAsync(second.GenerationId, first.GenerationId, ct);
        var act = () => repository.ActivateGenerationAsync(
            rival.GenerationId, first.GenerationId, ct).AsTask();

        // The predecessor is superseded, no longer quiescing, so the rival cannot win.
        await act.Should().ThrowAsync<WorkflowStateException>();
        (await repository.GetActiveGenerationAsync(instance.InstanceId, ct))!
            .GenerationId.Should().Be(second.GenerationId);
        (await repository.GetGenerationAsync(rival.GenerationId, ct))!
            .Status.Should().Be(WorkflowGenerationStatus.Prepared);
    }

    [Fact]
    public async Task RejectedCandidate_LeavesCurrentResumable()
    {
        var ct = TestContext.Current.CancellationToken;
        var repository = Store();
        var runId = await StartRunAsync(ct);
        var instance = await repository.CreateInstanceAsync(null, ct);
        var first = await ActivateAsync(repository, instance.InstanceId, runId, "plan-1", null, ct);
        var candidate = await repository.CreateGenerationAsync(
            instance.InstanceId, runId, "plan-2", "fp-2", first.GenerationId, ct);

        var rejected = await repository.RejectGenerationAsync(candidate.GenerationId, ct);

        rejected.Status.Should().Be(WorkflowGenerationStatus.Rejected);
        (await repository.GetActiveGenerationAsync(instance.InstanceId, ct))!
            .GenerationId.Should().Be(first.GenerationId);
        var reactivate = () => repository.ActivateGenerationAsync(
            candidate.GenerationId, first.GenerationId, ct).AsTask();
        await reactivate.Should().ThrowAsync<WorkflowStateException>();
    }

    [Fact]
    public async Task SupersededGeneration_NeverReactivates()
    {
        var ct = TestContext.Current.CancellationToken;
        var repository = Store();
        var runId = await StartRunAsync(ct);
        var instance = await repository.CreateInstanceAsync(null, ct);
        var first = await ActivateAsync(repository, instance.InstanceId, runId, "plan-1", null, ct);
        var second = await repository.CreateGenerationAsync(
            instance.InstanceId, runId, "plan-2", "fp-2", first.GenerationId, ct);
        await repository.PrepareGenerationAsync(second.GenerationId, ct);
        await repository.PauseGenerationAsync(first.GenerationId, ct);
        await repository.ActivateGenerationAsync(second.GenerationId, first.GenerationId, ct);

        var act = () => repository.ActivateGenerationAsync(
            first.GenerationId, expectedPredecessorGenerationId: null, ct).AsTask();

        await act.Should().ThrowAsync<WorkflowStateException>();
        (await repository.GetGenerationAsync(first.GenerationId, ct))!
            .Status.Should().Be(WorkflowGenerationStatus.Superseded);
    }

    [Fact]
    public async Task MissingEntities_FailWithNotFound()
    {
        var ct = TestContext.Current.CancellationToken;
        var repository = Store();

        (await repository.GetInstanceAsync(Guid.NewGuid(), ct)).Should().BeNull();
        (await repository.GetGenerationAsync(Guid.NewGuid(), ct)).Should().BeNull();
        await repository.Invoking(value => value.PrepareGenerationAsync(
                Guid.NewGuid(), ct).AsTask())
            .Should().ThrowAsync<WorkflowNotFoundException>();
        await repository.Invoking(value => value.ActivateGenerationAsync(
                Guid.NewGuid(), null, ct).AsTask())
            .Should().ThrowAsync<WorkflowNotFoundException>();
        await repository.Invoking(value => value.RejectGenerationAsync(
                Guid.NewGuid(), ct).AsTask())
            .Should().ThrowAsync<WorkflowNotFoundException>();
    }

    [Fact]
    public async Task CrashBetweenStates_ResumesCleanly()
    {
        var ct = TestContext.Current.CancellationToken;
        var runId = await StartRunAsync(ct);
        var crashed = Store();
        var instance = await crashed.CreateInstanceAsync("{\"k\":\"v\"}", ct);
        var created = await crashed.CreateGenerationAsync(
            instance.InstanceId, runId, "plan-1", "fp-1", null, ct);

        // Simulate a process kill: abandon the repository and reopen.
        var recovered = Store();
        (await recovered.GetInstanceAsync(instance.InstanceId, ct))
            .Should().BeEquivalentTo(instance);
        var resumed = await recovered.GetGenerationAsync(created.GenerationId, ct);

        resumed.Should().NotBeNull();
        resumed!.Status.Should().Be(WorkflowGenerationStatus.Created);
        var active = await recovered.ActivateGenerationAsync(
            (await recovered.PrepareGenerationAsync(created.GenerationId, ct)).GenerationId,
            expectedPredecessorGenerationId: null,
            ct);
        active.Status.Should().Be(WorkflowGenerationStatus.Active);
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
        var name = $"extop-{Guid.NewGuid():N}";
        var engine = CreateEngine(new SignalWorkflow(), name);
        return await engine.StartAsync(name, "1", "x", cancellationToken: ct);
    }

    private static async Task<WorkflowGeneration> ActivateAsync(
        SqliteWorkflowStore repository,
        Guid instanceId,
        Guid runId,
        string planRevision,
        Guid? predecessor,
        CancellationToken ct)
    {
        var created = await repository.CreateGenerationAsync(
            instanceId, runId, planRevision, "fp", predecessor, ct);
        var prepared = await repository.PrepareGenerationAsync(created.GenerationId, ct);
        return await repository.ActivateGenerationAsync(prepared.GenerationId, predecessor, ct);
    }
}

