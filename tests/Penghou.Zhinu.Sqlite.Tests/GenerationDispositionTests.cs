using FluentAssertions;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;

namespace Penghou.Zhinu.Sqlite.Tests;

/// <summary>
/// Typed checkpoint dispositions: accept, retry, and replan decisions are
/// recorded as append-only audit on a generation and never transition it;
/// acting on them stays explicit.
/// </summary>
public sealed class GenerationDispositionTests : WorkflowEngineTestBase
{
    [Fact]
    public async Task Record_Accept_PreservesGenerationState()
    {
        var ct = TestContext.Current.CancellationToken;
        var repository = Store();
        var runId = await StartRunAsync(ct);
        var instance = await repository.CreateInstanceAsync(null, ct);
        var candidate = await repository.CreateGenerationAsync(
            instance.InstanceId, runId, "plan-2", "fp-2", null, ct);
        await repository.PrepareGenerationAsync(candidate.GenerationId, ct);

        var recorded = await repository.RecordDispositionAsync(
            candidate.GenerationId, CheckpointDisposition.Accept, "reuse looks right", "reviewer", ct);

        recorded.GenerationId.Should().Be(candidate.GenerationId);
        recorded.Disposition.Should().Be(CheckpointDisposition.Accept);
        recorded.Reason.Should().Be("reuse looks right");
        recorded.Actor.Should().Be("reviewer");
        recorded.CreatedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
        (await repository.GetGenerationAsync(candidate.GenerationId, ct))!
            .Status.Should().Be(WorkflowGenerationStatus.Prepared);
    }

    [Fact]
    public async Task Dispositions_AppendInRecordedOrder()
    {
        var ct = TestContext.Current.CancellationToken;
        var repository = Store();
        var runId = await StartRunAsync(ct);
        var instance = await repository.CreateInstanceAsync(null, ct);
        var candidate = await repository.CreateGenerationAsync(
            instance.InstanceId, runId, "plan-2", "fp-2", null, ct);

        await repository.RecordDispositionAsync(
            candidate.GenerationId, CheckpointDisposition.Retry, "re-evaluate costs", null, ct);
        await repository.RecordDispositionAsync(
            candidate.GenerationId, CheckpointDisposition.Replan, "needs new plan", "lead", ct);

        var listed = await repository.ListDispositionsAsync(candidate.GenerationId, ct);
        listed.Select(item => item.Disposition)
            .Should().Equal(CheckpointDisposition.Retry, CheckpointDisposition.Replan);
        listed.Select(item => item.GenerationId).Should()
            .OnlyContain(id => id == candidate.GenerationId);
        listed[0].Actor.Should().BeNull();
        listed[1].Reason.Should().Be("needs new plan");
    }

    [Fact]
    public async Task Dispositions_AreScopedPerGeneration()
    {
        var ct = TestContext.Current.CancellationToken;
        var repository = Store();
        var runId = await StartRunAsync(ct);
        var instance = await repository.CreateInstanceAsync(null, ct);
        var first = await repository.CreateGenerationAsync(
            instance.InstanceId, runId, "plan-1", "fp-1", null, ct);
        var second = await repository.CreateGenerationAsync(
            instance.InstanceId, runId, "plan-2", "fp-2", first.GenerationId, ct);

        await repository.RecordDispositionAsync(
            first.GenerationId, CheckpointDisposition.Accept, null, null, ct);

        (await repository.ListDispositionsAsync(second.GenerationId, ct)).Should().BeEmpty();
        (await repository.ListDispositionsAsync(first.GenerationId, ct)).Should().ContainSingle();
    }

    [Fact]
    public async Task Record_MissingGeneration_FailsWithNotFound()
    {
        var ct = TestContext.Current.CancellationToken;
        var repository = Store();

        var act = () => repository.RecordDispositionAsync(
            Guid.NewGuid(), CheckpointDisposition.Accept, null, null, ct).AsTask();

        await act.Should().ThrowAsync<WorkflowNotFoundException>();
        (await repository.ListDispositionsAsync(Guid.NewGuid(), ct)).Should().BeEmpty();
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
        var name = $"gen-disposition-{Guid.NewGuid():N}";
        var engine = CreateEngine(new SignalWorkflow(), name);
        return await engine.StartAsync(name, "1", "x", cancellationToken: ct);
    }
}
