using FluentAssertions;

namespace Penghou.Zhinu.Sqlite.Tests;

public sealed class ActivityGroupSignalWaitTests : WorkflowEngineTestBase
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task GroupedSignalWaitProjectsWaitingMembership_AndReexecutesWithSameIdentity()
    {
        var store = CreateStore();
        var registry = Registry(foreignGroup: false);
        Guid runId;
        WorkflowStepRun originalStep;
        ActivityGroupStepMembership originalMembership;

        await using (var engine = new WorkflowEngine(store, registry))
        {
            runId = await engine.StartAsync("grouped-signal", "1", "input", cancellationToken: Ct);
            using var interruption = new CancellationTokenSource();
            var execution = engine.ExecuteAsync(runId, interruption.Token);
            await WaitUntilAsync(
                () => HasStepStatusAsync(engine, runId, "approval", StepStatus.Waiting, Ct), Ct);

            originalStep = (await engine.GetStepsAsync(runId, Ct)).Single(step => step.StepKey == "approval");
            originalMembership = (await store.GetStepGroupMembershipAsync(originalStep.Id, Ct))!;
            originalMembership.Should().NotBeNull();
            originalMembership.StepRevision.Should().Be(originalStep.Revision);

            using var reader = new WorkflowGroupReader(CreateStore());
            var snapshot = (await reader.GetRunGroupSnapshotAsync(runId, Ct)).Value!;
            var rootNodes = await reader.QueryGroupChildrenAsync(runId, snapshot.SnapshotToken,
                pageSize: 10, cancellationToken: Ct);
            var occurrence = rootNodes.Value!.Items.Single(node => node.Occurrence is not null).Occurrence!;
            occurrence.OccurrenceId.Should().Be(originalMembership.GroupOccurrenceId);
            var state = await reader.GetGroupStateAsync(runId, snapshot.SnapshotToken, occurrence.OccurrenceId, Ct);
            state.Status.Should().Be(WorkflowGroupReadStatus.Success);
            state.Value!.State.Status.Should().Be(StepStatus.Waiting);
            state.Value.State.Work.Waiting.Should().Be(1);
            var members = await reader.QueryGroupMembersAsync(runId, snapshot.SnapshotToken,
                occurrence.OccurrenceId, pageSize: 10, cancellationToken: Ct);
            members.Value!.Items.Should().ContainSingle(member =>
                member.Step.StepId == originalStep.Id && member.Step.Revision == originalStep.Revision &&
                member.Step.Status == StepStatus.Waiting && member.Step.GroupOccurrenceId == occurrence.OccurrenceId);

            await interruption.CancelAsync();
            await execution;
        }

        await using (var reopened = new WorkflowEngine(CreateStore(), registry))
        {
            await reopened.SendSignalAsync(runId, "approve", "accepted", Ct);
            await reopened.ExecuteAsync(runId, Ct);
            (await reopened.WaitForCompletionAsync<string>(runId, cancellationToken: Ct)).Should().Be("accepted");
            var completedStep = (await reopened.GetStepsAsync(runId, Ct)).Single(step => step.StepKey == "approval");
            completedStep.Id.Should().Be(originalStep.Id);
            completedStep.Revision.Should().Be(originalStep.Revision);
            completedStep.Status.Should().Be(StepStatus.Completed);
            var completedMembership = await CreateStore().GetStepGroupMembershipAsync(completedStep.Id, Ct);
            completedMembership.Should().BeEquivalentTo(originalMembership);
        }
    }

    [Fact]
    public async Task ForeignGroupBindingRejectsBeforeCreatingWaitClaim()
    {
        var store = CreateStore();
        await using var engine = new WorkflowEngine(store, Registry(foreignGroup: true));
        var runId = await engine.StartAsync("grouped-signal", "1", "input", cancellationToken: Ct);

        await engine.ExecuteAsync(runId, Ct);

        (await store.GetStepsAsync(runId, Ct)).Should().NotContain(step => step.StepKey == "approval");
        (await store.GetRunAsync(runId, Ct))!.Status.Should().Be(WorkflowStatus.Failed);
    }

    private static WorkflowRegistry Registry(bool foreignGroup) => new WorkflowRegistry().Register(
        new WorkflowRegistration<string, string>(
            new WorkflowDefinition { Name = "grouped-signal", Version = "1" },
            () => new GroupedSignalWorkflow(foreignGroup), GroupedSignalWorkflow.Manifest()));

    private sealed class GroupedSignalWorkflow(bool foreignGroup) : IWorkflow<string, string>
    {
        public Task<string> RunAsync(WorkflowContext context, string input, CancellationToken cancellationToken)
        {
            var occurrence = foreignGroup
                ? ActivityGroupOccurrence.Create(Guid.NewGuid(), Manifest(), "root", null, "[]")
                : context.GetActivityGroup("root");
            var binding = new ActivityGroupStepBinding(new ActivityGroupMembership("root"), occurrence);
            return context.WaitForGroupedSignalAsync<string>("approval", "approve", binding,
                cancellationToken: cancellationToken);
        }

        internal static ActivityGroupManifest Manifest() => ActivityGroupManifest.Create([
            new ActivityGroupDefinition("root", ActivityGroupKind.Logical, "Root")
        ]);
    }
}
