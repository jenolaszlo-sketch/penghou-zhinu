using FluentAssertions;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;

namespace Penghou.Zhinu.Sqlite.Tests;

/// <summary>
/// Compensation is preserved independently of plan returns: returning to an
/// earlier plan creates a later generation and never erases or rewinds
/// execution history, while dispositions and prior generations stay auditable.
/// (Compensation rows surviving restart invalidation is covered by
/// <c>CompensationTests.StepAsync_CompensatedStepRestart_CreatesCompensationForNewRevision</c>.)
/// </summary>
public sealed class GenerationPlanReturnTests : WorkflowEngineTestBase
{
    [Fact]
    public async Task ReturnToEarlierPlan_CreatesLaterGenerationWithoutErasingHistory()
    {
        var ct = TestContext.Current.CancellationToken;
        var repository = Store();
        var runId = await StartRunAsync(ct);
        var instance = await repository.CreateInstanceAsync(null, ct);

        var first = await ActivateAsync(repository, instance.InstanceId, runId, "plan-A", null, ct);
        await repository.PauseGenerationAsync(first.GenerationId, ct);
        var second = await ActivateAsync(
            repository, instance.InstanceId, runId, "plan-B", first.GenerationId, ct);

        // Return to the earlier plan lineage through a new generation.
        await repository.PauseGenerationAsync(second.GenerationId, ct);
        var created = await repository.CreateGenerationAsync(
            instance.InstanceId, runId, "plan-A", "fp-A3", second.GenerationId, ct);
        created.Ordinal.Should().Be(3);
        await repository.PrepareGenerationAsync(created.GenerationId, ct);
        await repository.RecordDispositionAsync(
            created.GenerationId, CheckpointDisposition.Replan, "plan-B rejected in review", "lead", ct);
        var third = await repository.ActivateGenerationAsync(created.GenerationId, second.GenerationId, ct);

        third.Status.Should().Be(WorkflowGenerationStatus.Active);
        third.PlanRevision.Should().Be("plan-A");
        third.PredecessorGenerationId.Should().Be(second.GenerationId);
        (await repository.ListGenerationsAsync(instance.InstanceId, ct))
            .Select(item => (item.Ordinal, item.PlanRevision, item.Status))
            .Should().Equal(
                (1, "plan-A", WorkflowGenerationStatus.Superseded),
                (2, "plan-B", WorkflowGenerationStatus.Superseded),
                (3, "plan-A", WorkflowGenerationStatus.Active));
    }

    [Fact]
    public async Task SupersededGenerations_KeepDispositionsAuditable()
    {
        var ct = TestContext.Current.CancellationToken;
        var repository = Store();
        var runId = await StartRunAsync(ct);
        var instance = await repository.CreateInstanceAsync(null, ct);
        var first = await ActivateAsync(repository, instance.InstanceId, runId, "plan-A", null, ct);
        await repository.RecordDispositionAsync(
            first.GenerationId, CheckpointDisposition.Accept, "initial rollout", "lead", ct);

        await repository.PauseGenerationAsync(first.GenerationId, ct);
        var second = await ActivateAsync(
            repository, instance.InstanceId, runId, "plan-B", first.GenerationId, ct);

        // Audit recorded before cutover survives, and audit-after-fact stays possible.
        (await repository.ListDispositionsAsync(first.GenerationId, ct))
            .Should().ContainSingle()
            .Which.Disposition.Should().Be(CheckpointDisposition.Accept);
        await repository.RecordDispositionAsync(
            first.GenerationId, CheckpointDisposition.Retry, "post-cutover review note", "lead", ct);
        (await repository.ListDispositionsAsync(first.GenerationId, ct))
            .Select(item => item.Disposition)
            .Should().Equal(CheckpointDisposition.Accept, CheckpointDisposition.Retry);
        (await repository.GetGenerationAsync(second.GenerationId, ct))!
            .PredecessorGenerationId.Should().Be(first.GenerationId);
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
        var name = $"gen-return-{Guid.NewGuid():N}";
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
        if (predecessor is not null)
        {
            var owner = (await repository.GetGenerationAsync(predecessor.Value, ct))!;
            if (owner.Status == WorkflowGenerationStatus.Active)
                await repository.PauseGenerationAsync(predecessor.Value, ct);
        }

        return await repository.ActivateGenerationAsync(prepared.GenerationId, predecessor, ct);
    }
}
