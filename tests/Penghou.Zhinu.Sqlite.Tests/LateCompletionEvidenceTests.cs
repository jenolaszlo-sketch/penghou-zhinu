using FluentAssertions;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;

namespace Penghou.Zhinu.Sqlite.Tests;

/// <summary>
/// Late-completion fencing for steps: a completion refused on fencing grounds
/// retains its result and provenance as a durable evidence event while
/// advancing nothing — no state change, no successors.
/// </summary>
public sealed class LateCompletionEvidenceTests : WorkflowEngineTestBase
{
    [Fact]
    public async Task RefusedCompletion_RetainsResultAndProvenanceWithoutAdvancing()
    {
        var ct = TestContext.Current.CancellationToken;
        var engine = CreateEngine(new SignalWorkflow(), "late-completion");
        var runId = await engine.StartAsync("late-completion", "1", "x", cancellationToken: ct);
        var store = PeerStore();
        var now = DateTimeOffset.UtcNow;
        var claim = await store.ClaimStepAsync(
            new StepClaimRequest
            {
                WorkflowRunId = runId,
                StepKey = "work",
                OutputType = "string",
                OwnerId = "worker-1",
                Now = now,
                LeaseExpiresAt = now.AddMinutes(5)
            },
            ct);
        claim.Disposition.Should().Be(StepClaimDisposition.Acquired);

        var act = () => store.CompleteStepAsync(
            claim.Step.Id, "worker-2", "\"late-output\"", now, ct).AsTask();

        await act.Should().ThrowAsync<WorkflowStateException>();
        var events = await store.GetEventsAsync(runId, 0, 100, ct);
        var evidence = events
            .Should().ContainSingle(item =>
                item.EventType == WorkflowEventTypes.StepCompletionRefused)
            .Which;
        evidence.EventType.Should().Be(WorkflowEventTypes.StepCompletionRefused);
        evidence.StepKey.Should().Be("work");
        evidence.DataJson.Should().Contain("late-output").And.Contain("worker-2");
        // Nothing advanced: the lease still belongs to the rightful owner.
        var step = (await store.GetStepsAsync(runId, ct)).Should().ContainSingle().Subject;
        step.Status.Should().Be(StepStatus.Running);
        step.LeaseOwner.Should().Be("worker-1");
        step.OutputJson.Should().BeNull();
    }

    [Fact]
    public async Task OwnedCompletion_RecordsNoRefusalEvidence()
    {
        var ct = TestContext.Current.CancellationToken;
        var engine = CreateEngine(new SignalWorkflow(), "late-completion-owned");
        var runId = await engine.StartAsync(
            "late-completion-owned", "1", "x", cancellationToken: ct);
        var store = PeerStore();
        var now = DateTimeOffset.UtcNow;
        var claim = await store.ClaimStepAsync(
            new StepClaimRequest
            {
                WorkflowRunId = runId,
                StepKey = "work",
                OutputType = "string",
                OwnerId = "worker-1",
                Now = now,
                LeaseExpiresAt = now.AddMinutes(5)
            },
            ct);

        await store.CompleteStepAsync(claim.Step.Id, "worker-1", "\"ok\"", now, ct);

        (await store.GetEventsAsync(runId, 0, 100, ct)).Should()
            .ContainSingle(item => item.EventType == WorkflowEventTypes.StepCompleted)
            .And.NotContain(item => item.EventType == WorkflowEventTypes.StepCompletionRefused);
    }

    private SqliteWorkflowStore PeerStore() =>
        new(new ZhinuSqliteOptions
        {
            DatabasePath = Path.Combine(root, "zhinu.db"),
            BusyTimeout = TimeSpan.FromSeconds(2),
            Pooling = false
        });
}
