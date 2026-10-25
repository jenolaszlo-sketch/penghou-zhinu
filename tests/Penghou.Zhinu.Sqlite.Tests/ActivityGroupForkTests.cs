using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.ExceptionServices;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Penghou.Workflow.Abstractions;

namespace Penghou.Zhinu.Sqlite.Tests;

public sealed class ActivityGroupForkTests : WorkflowEngineTestBase
{
    private const string WorkflowName = "grouped-fork-contract";
    private const string SubjectBinding = "tenant-1:alice:project:workspace-1";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ForkAsync_CopiesLineageIntoIndependentSiblingRunsAndKeepsJoinUngrouped()
    {
        var store = CreateStore();
        var probe = new ForkProbe();
        var manifest = CreateManifest();
        var registry = Registry(probe, manifest, manifest);
        await using var engine = CreateEngine(store, registry);
        var sourceId = await engine.StartAsync(
            WorkflowName, "1", "payload",
            authoritySubjectBinding: SubjectBinding,
            cancellationToken: Ct);
        await engine.ExecuteAsync(sourceId, Ct);
        (await engine.WaitForCompletionAsync<string>(sourceId, cancellationToken: Ct))
            .Should().Be("left:payload|right:payload");
        var sourceBefore = await CaptureRunAsync(store, sourceId);

        var leftForkId = Guid.NewGuid();
        var rightForkId = Guid.NewGuid();
        (await engine.ForkAsync(sourceId, "left", new ForkRunOptions
        {
            WorkflowRunId = leftForkId,
            Actor = "reviewer",
            Reason = "revise left branch"
        }, Ct)).Should().Be(leftForkId);
        (await engine.ForkAsync(sourceId, "right", new ForkRunOptions
        {
            WorkflowRunId = rightForkId,
            Actor = "reviewer",
            Reason = "revise right branch"
        }, Ct)).Should().Be(rightForkId);

        foreach (var runId in new[] { leftForkId, rightForkId })
        {
            await engine.ExecuteAsync(runId, Ct);
            (await engine.WaitForCompletionAsync<string>(runId, cancellationToken: Ct))
                .Should().Be("left:payload|right:payload");
            var forkRun = (await store.GetRunAsync(runId, Ct))!;
            forkRun.SourceRunId.Should().Be(sourceId);
            forkRun.AuthoritySubjectBinding.Should().Be(SubjectBinding);
            var memberships = await ReadMembershipsAsync(store, runId);
            memberships.Select(item => item.StepKey).Should().BeEquivalentTo("left", "right");
            (await MembershipAsync(store, runId, "left"))!.Role.Should().Be(ActivityGroupMemberRole.Work);
            (await MembershipAsync(store, runId, "right"))!.Role.Should().Be(ActivityGroupMemberRole.Work);
            (await MembershipAsync(store, runId, "join")).Should().BeNull(
                "an ordinary join step does not inherit lineage from its grouped dependencies");

            var rootOccurrence = await store.GetGroupOccurrenceAsync(
                runId,
                RootOccurrence(runId, manifest).GroupOccurrenceId,
                Ct);
            rootOccurrence.Should().NotBeNull();
            foreach (var stepKey in new[] { "left", "right" })
            {
                var membership = (await MembershipAsync(store, runId, stepKey))!;
                var occurrence = await store.GetGroupOccurrenceAsync(runId, membership.GroupOccurrenceId, Ct);
                occurrence.Should().NotBeNull();
                occurrence!.WorkflowRunId.Should().Be(runId);
                occurrence.ParentGroupOccurrenceId.Should().Be(rootOccurrence!.GroupOccurrenceId);
                occurrence.GroupKey.Should().Be(stepKey);
            }
        }

        var sourceAfter = await CaptureRunAsync(store, sourceId);
        sourceAfter.Should().BeEquivalentTo(sourceBefore);
        var sourceLeft = (await MembershipAsync(store, sourceId, "left"))!;
        var leftForkLeft = (await MembershipAsync(store, leftForkId, "left"))!;
        var rightForkLeft = (await MembershipAsync(store, rightForkId, "left"))!;
        leftForkLeft.GroupOccurrenceId.Should().NotBe(sourceLeft.GroupOccurrenceId);
        rightForkLeft.GroupOccurrenceId.Should().NotBe(sourceLeft.GroupOccurrenceId);
        rightForkLeft.GroupOccurrenceId.Should().NotBe(leftForkLeft.GroupOccurrenceId);

        probe.CallsFor(sourceId, "left").Should().Be(1);
        probe.CallsFor(sourceId, "right").Should().Be(1);
        probe.CallsFor(leftForkId, "left").Should().Be(1);
        probe.CallsFor(leftForkId, "right").Should().Be(0);
        probe.CallsFor(leftForkId, "join").Should().Be(1);
        probe.CallsFor(rightForkId, "left").Should().Be(0);
        probe.CallsFor(rightForkId, "right").Should().Be(1);
        probe.CallsFor(rightForkId, "join").Should().Be(1);
    }

    [Fact]
    public async Task ForkAsync_ReexecutesOnlyBlockedBranchAndReusesCompletedSiblingMembership()
    {
        var store = CreateStore();
        var probe = new ForkProbe { FailRight = true };
        var manifest = CreateManifest();
        await using var engine = CreateEngine(store, Registry(probe, manifest, manifest));
        var sourceId = await engine.StartAsync(WorkflowName, "1", "payload", cancellationToken: Ct);
        await engine.ExecuteAsync(sourceId, Ct);
        var sourceFailure = () => engine.WaitForCompletionAsync<string>(sourceId, cancellationToken: Ct);
        await sourceFailure.Should().ThrowAsync<WorkflowExecutionFailedException>();

        (await store.GetStepsAsync(sourceId, Ct)).Single(step => step.StepKey == "left")
            .Status.Should().Be(StepStatus.Completed);
        (await store.GetStepsAsync(sourceId, Ct)).Single(step => step.StepKey == "right")
            .Status.Should().Be(StepStatus.Failed);
        (await store.GetStepsAsync(sourceId, Ct)).Should().NotContain(step => step.StepKey == "join");
        var sourceLeftMembership = (await MembershipAsync(store, sourceId, "left"))!;
        var sourceRightMembership = (await MembershipAsync(store, sourceId, "right"))!;
        probe.FailRight = false;

        var forkId = await engine.ForkAsync(sourceId, "right", new ForkRunOptions
        {
            Mode = StepRestartMode.Dependents
        }, Ct);
        await engine.ExecuteAsync(forkId, Ct);
        (await engine.WaitForCompletionAsync<string>(forkId, cancellationToken: Ct))
            .Should().Be("left:payload|right:payload");

        probe.CallsFor(forkId, "left").Should().Be(0);
        probe.CallsFor(forkId, "right").Should().Be(1);
        probe.CallsFor(forkId, "join").Should().Be(1);
        var forkLeftMembership = (await MembershipAsync(store, forkId, "left"))!;
        var forkRightMembership = (await MembershipAsync(store, forkId, "right"))!;
        forkLeftMembership.GroupOccurrenceId.Should().NotBe(sourceLeftMembership.GroupOccurrenceId);
        forkRightMembership.GroupOccurrenceId.Should().NotBe(sourceRightMembership.GroupOccurrenceId);
        (await MembershipAsync(store, forkId, "join")).Should().BeNull();
    }

    [Fact]
    public async Task Retry_ReusesOneGroupOccurrenceForEveryAttemptOfTheStepRevision()
    {
        var store = CreateStore();
        var probe = new ForkProbe();
        var manifest = CreateManifest();
        await using var engine = CreateEngine(store, Registry(probe, manifest, manifest));
        var sourceId = await engine.StartAsync(WorkflowName, "1", "payload", cancellationToken: Ct);
        await engine.ExecuteAsync(sourceId, Ct);
        (await engine.WaitForCompletionAsync<string>(sourceId, cancellationToken: Ct))
            .Should().Be("left:payload|right:payload");
        probe.FailLeftFirstAttempt = true;

        var forkId = await engine.ForkAsync(sourceId, "left", new ForkRunOptions
        {
            Mode = StepRestartMode.StepOnly
        }, Ct);
        await engine.ExecuteAsync(forkId, Ct);
        (await engine.WaitForCompletionAsync<string>(forkId, cancellationToken: Ct))
            .Should().Be("left:payload|right:payload");

        var leftStep = (await store.GetStepsAsync(forkId, Ct)).Single(step => step.StepKey == "left");
        leftStep.Attempt.Should().Be(2);
        leftStep.Revision.Should().Be(1);
        var leftAttempts = probe.AttemptsFor(forkId, "left");
        leftAttempts.Select(item => item.Attempt).Should().Equal(1, 2);
        leftAttempts.Select(item => item.StepExecutionId).Distinct().Should().ContainSingle();
        leftAttempts.Select(item => item.Revision).Distinct().Should().ContainSingle().Which.Should().Be(1);
        var membership = await MembershipAsync(store, forkId, "left");
        membership.Should().NotBeNull();
        membership!.StepId.Should().Be(leftStep.Id);
        membership.StepRevision.Should().Be(leftStep.Revision);
        membership.GroupOccurrenceId.Should().NotBe((await MembershipAsync(store, sourceId, "left"))!.GroupOccurrenceId);
    }

    [Fact]
    public async Task RestartWhileGroupedBranchIsRunning_FencesOldAttemptAndKeepsCompletedSibling()
    {
        var store = CreateStore();
        var probe = new ForkProbe();
        var manifest = CreateManifest();
        await using var engine = CreateEngine(store, Registry(probe, manifest, manifest));
        var sourceId = await engine.StartAsync(WorkflowName, "1", "payload", cancellationToken: Ct);
        await engine.ExecuteAsync(sourceId, Ct);
        await engine.WaitForCompletionAsync<string>(sourceId, cancellationToken: Ct);

        var forkId = Guid.NewGuid();
        probe.BlockRightRunId = forkId;
        var forked = await engine.ForkAsync(sourceId, "right", new ForkRunOptions
        {
            WorkflowRunId = forkId,
            Mode = StepRestartMode.StepOnly
        }, Ct);
        forked.Should().Be(forkId);
        var staleExecution = engine.ExecuteAsync(forkId, Ct);
        await probe.RightEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        var leftBefore = (await store.GetStepsAsync(forkId, Ct)).Single(step => step.StepKey == "left");
        var leftMembershipBefore = (await MembershipAsync(store, forkId, "left"))!;
        var rightBefore = (await store.GetStepsAsync(forkId, Ct)).Single(step => step.StepKey == "right");
        rightBefore.Status.Should().Be(StepStatus.Running);

        var restart = await engine.RestartStepAsync(forkId, "right",
            new RestartStepOptions { Mode = StepRestartMode.StepOnly }, Ct);
        restart.StepsToInvalidate.Should().ContainSingle(step => step.StepKey == "right");
        var restartedSteps = await store.GetStepsAsync(forkId, Ct);
        restartedSteps.Single(step => step.StepKey == "left").Should().Be(leftBefore);
        var rightRevision = restartedSteps.Single(step => step.StepKey == "right");
        rightRevision.Revision.Should().Be(rightBefore.Revision + 1);
        rightRevision.Status.Should().Be(StepStatus.Pending);
        var leftMembershipAfter = (await MembershipAsync(store, forkId, "left"))!;
        leftMembershipAfter.Should().Be(leftMembershipBefore);

        probe.ReleaseRight.TrySetResult(true);
        try
        {
            await staleExecution.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        }
        catch (OperationCanceledException) { }
        catch (WorkflowConcurrencyException) { }
        catch (WorkflowExecutionFailedException) { }
        (await store.GetRunAsync(forkId, Ct))!.Status.Should().Be(WorkflowStatus.Pending);
        probe.BlockRightRunId = null;
        await engine.ExecuteAsync(forkId, Ct);
        (await engine.WaitForCompletionAsync<string>(forkId, cancellationToken: Ct))
            .Should().Be("left:payload|right:payload");

        probe.CallsFor(forkId, "left").Should().Be(0);
        probe.CallsFor(forkId, "right").Should().Be(2);
        (await store.GetStepsAsync(forkId, Ct)).Single(step => step.StepKey == "left").Should().Be(leftBefore);
        (await MembershipAsync(store, forkId, "left")).Should().Be(leftMembershipBefore);
        var rightMembership = (await MembershipAsync(store, forkId, "right"))!;
        rightMembership.StepRevision.Should().Be(rightRevision.Revision);
    }

    [Fact]
    public async Task VersionMutation_RebindsReusableMembershipAndClonesItForSupersedingRevision()
    {
        var store = CreateStore();
        var probe = new ForkProbe();
        var sourceManifest = CreateManifest();
        var targetManifest = CreateManifest(
            leftDisplay: "Renamed left group",
            rightDisplay: "Renamed right group",
            leftOrdinal: 1,
            rightOrdinal: 0);
        await using var engine = CreateEngine(store, Registry(probe, sourceManifest, targetManifest));
        var sourceId = await engine.StartAsync(WorkflowName, "1", "payload", cancellationToken: Ct);
        await engine.ExecuteAsync(sourceId, Ct);
        (await engine.WaitForCompletionAsync<string>(sourceId, cancellationToken: Ct))
            .Should().Be("left:payload|right:payload");
        var sourceSnapshot = await CaptureRunAsync(store, sourceId);

        var forkId = await engine.ForkAsync(sourceId, "right", new ForkRunOptions
        {
            TargetWorkflowVersion = "2",
            Mode = StepRestartMode.StepOnly
        }, Ct);
        var copiedLeftStep = (await store.GetStepsAsync(forkId, Ct)).Single(step => step.StepKey == "left");
        copiedLeftStep.Revision.Should().Be(1);
        var copiedLeftMembership = (await store.GetStepGroupMembershipAsync(copiedLeftStep.Id, Ct))!;
        await engine.ExecuteAsync(forkId, Ct);
        (await engine.WaitForCompletionAsync<string>(forkId, cancellationToken: Ct))
            .Should().Be("left:v2:payload|right:payload");

        (await store.GetGroupManifestAsync(forkId, Ct))!.CanonicalJson.Should().Be(targetManifest.CanonicalJson);
        var leftSteps = (await store.GetStepsAsync(forkId, Ct)).Where(step => step.StepKey == "left")
            .OrderBy(step => step.Revision).ToArray();
        leftSteps.Should().ContainSingle();
        leftSteps[0].Revision.Should().Be(2);
        leftSteps[0].Status.Should().Be(StepStatus.Completed);
        var supersedingMembership = (await store.GetStepGroupMembershipAsync(leftSteps[0].Id, Ct))!;
        supersedingMembership.StepRevision.Should().Be(2);
        supersedingMembership.GroupOccurrenceId.Should().Be(copiedLeftMembership.GroupOccurrenceId);
        (await store.GetStepGroupMembershipAsync(copiedLeftStep.Id, Ct)).Should().Be(copiedLeftMembership);
        var destinationRoot = RootOccurrence(forkId, targetManifest);
        var expectedLeft = ActivityGroupOccurrence.Create(
            forkId, targetManifest, "left", destinationRoot, "[]");
        copiedLeftMembership.GroupOccurrenceId.Should().Be(expectedLeft.GroupOccurrenceId);
        (await CaptureRunAsync(store, sourceId)).Should().BeEquivalentTo(sourceSnapshot);
    }

    [Theory]
    [InlineData("missing-manifest")]
    [InlineData("changed-parent")]
    [InlineData("changed-provenance")]
    public async Task ForkAsync_IncompatibleTargetAbortsWithoutDestination(string targetCase)
    {
        var store = CreateStore();
        var probe = new ForkProbe();
        var sourceManifest = CreateManifest();
        var targetManifest = targetCase switch
        {
            "missing-manifest" => (ActivityGroupManifest?)null,
            "changed-parent" => CreateManifest(leftParent: "alternate", alternateOrdinal: 2),
            "changed-provenance" => CreateManifest(leftProvenance: new ActivityGroupMacroProvenance(
                "pipeline", "fingerprint-v2", "invocation")),
            _ => throw new ArgumentOutOfRangeException(nameof(targetCase))
        };
        var registry = Registry(probe, sourceManifest, targetManifest);
        await using var engine = CreateEngine(store, registry);
        var sourceId = await engine.StartAsync(WorkflowName, "1", "payload", cancellationToken: Ct);
        await engine.ExecuteAsync(sourceId, Ct);
        await engine.WaitForCompletionAsync<string>(sourceId, cancellationToken: Ct);
        var sourceBefore = await CaptureRunAsync(store, sourceId);
        var destinationId = Guid.NewGuid();
        var beforeRuns = await engine.GetRunsAsync(new RunQuery(), Ct);

        var fork = () => engine.ForkAsync(sourceId, "right", new ForkRunOptions
        {
            TargetWorkflowVersion = "2",
            WorkflowRunId = destinationId
        }, Ct);
        await fork.Should().ThrowAsync<ZhinuException>();

        (await store.GetRunAsync(destinationId, Ct)).Should().BeNull();
        (await engine.GetRunsAsync(new RunQuery(), Ct)).Should().BeEquivalentTo(beforeRuns);
        (await CaptureRunAsync(store, sourceId)).Should().BeEquivalentTo(sourceBefore);
    }

    [Fact]
    public async Task ForkAsync_RequiresOptionalGroupedForkCapabilityBeforeAnyWrite()
    {
        var backingStore = CreateStore();
        var probe = new ForkProbe();
        var manifest = CreateManifest();
        var registry = Registry(probe, manifest, manifest);
        await using (var sourceEngine = CreateEngine(backingStore, registry))
        {
            var sourceId = await sourceEngine.StartAsync(WorkflowName, "1", "payload", cancellationToken: Ct);
            await sourceEngine.ExecuteAsync(sourceId, Ct);
            await sourceEngine.WaitForCompletionAsync<string>(sourceId, cancellationToken: Ct);
            var sourceBefore = await CaptureRunAsync(backingStore, sourceId);
            var beforeRuns = await sourceEngine.GetRunsAsync(new RunQuery(), Ct);
            var destinationId = Guid.NewGuid();
            var hiddenStore = HideGroupForkCapability(backingStore);
            await using var hiddenEngine = CreateEngine(hiddenStore, registry);

            var fork = () => hiddenEngine.ForkAsync(sourceId, "left", new ForkRunOptions
            {
                WorkflowRunId = destinationId
            }, Ct);
            await fork.Should().ThrowAsync<WorkflowConfigurationException>();

            (await backingStore.GetRunAsync(destinationId, Ct)).Should().BeNull();
            (await sourceEngine.GetRunsAsync(new RunQuery(), Ct)).Should().BeEquivalentTo(beforeRuns);
            (await CaptureRunAsync(backingStore, sourceId)).Should().BeEquivalentTo(sourceBefore);
        }
    }

    [Fact]
    public async Task OrdinarySourceCanForkToGroupedAndUngroupedVersionsWithoutInventingPrefixMembership()
    {
        var store = CreateStore();
        var probe = new ForkProbe();
        var targetManifest = ActivityGroupManifest.Create([
            new ActivityGroupDefinition("root", ActivityGroupKind.Logical, "Root"),
            new ActivityGroupDefinition("added", ActivityGroupKind.Logical, "Added grouped work", "root")
        ]);
        var registry = new WorkflowRegistry()
            .Register(new WorkflowRegistration<string, string>(
                new WorkflowDefinition { Name = WorkflowName, Version = "1" },
                () => new PrefixForkWorkflow(probe, grouped: false)))
            .Register(new WorkflowRegistration<string, string>(
                new WorkflowDefinition { Name = WorkflowName, Version = "2" },
                () => new PrefixForkWorkflow(probe, grouped: true), targetManifest))
            .Register(new WorkflowRegistration<string, string>(
                new WorkflowDefinition { Name = WorkflowName, Version = "3" },
                () => new PrefixForkWorkflow(probe, grouped: false)));
        await using var engine = CreateEngine(store, registry);
        var sourceId = await engine.StartAsync(WorkflowName, "1", "payload", cancellationToken: Ct);
        await engine.ExecuteAsync(sourceId, Ct);
        (await engine.WaitForCompletionAsync<string>(sourceId, cancellationToken: Ct)).Should().Be("work:payload");
        (await MembershipAsync(store, sourceId, "before")).Should().BeNull();
        (await MembershipAsync(store, sourceId, "work")).Should().BeNull();

        var groupedFork = await engine.ForkAsync(sourceId, "finish", new ForkRunOptions
        {
            TargetWorkflowVersion = "2",
            Mode = StepRestartMode.StepOnly
        }, Ct);
        await engine.ExecuteAsync(groupedFork, Ct);
        (await engine.WaitForCompletionAsync<string>(groupedFork, cancellationToken: Ct)).Should().Be("work:payload");
        (await store.GetGroupManifestAsync(groupedFork, Ct))!.ManifestDigest.Should().Be(targetManifest.ManifestDigest);
        (await MembershipAsync(store, groupedFork, "before")).Should().BeNull();
        (await MembershipAsync(store, groupedFork, "work")).Should().BeNull(
            "copied ungrouped source steps remain ungrouped even in a grouped destination");
        (await MembershipAsync(store, groupedFork, "added")).Should().NotBeNull();
        probe.CallsFor(groupedFork, "work").Should().Be(0);
        probe.CallsFor(groupedFork, "added").Should().Be(1);

        var ungroupedFork = await engine.ForkAsync(sourceId, "finish", new ForkRunOptions
        {
            TargetWorkflowVersion = "3",
            Mode = StepRestartMode.StepOnly
        }, Ct);
        await engine.ExecuteAsync(ungroupedFork, Ct);
        (await engine.WaitForCompletionAsync<string>(ungroupedFork, cancellationToken: Ct)).Should().Be("work:payload");
        (await store.GetGroupManifestAsync(ungroupedFork, Ct)).Should().BeNull();
        (await MembershipAsync(store, ungroupedFork, "before")).Should().BeNull();
        (await MembershipAsync(store, ungroupedFork, "work")).Should().BeNull();
    }

    [Fact]
    public async Task GroupedForkPreservesSubjectAndDoesNotCopyAuthorizationEvidence()
    {
        var store = CreateStore();
        var manifest = ActivityGroupManifest.Create([
            new ActivityGroupDefinition("root", ActivityGroupKind.Logical, "Root"),
            new ActivityGroupDefinition("protected", ActivityGroupKind.Macro, "Protected", "root",
                provenance: new ActivityGroupMacroProvenance("guarded", "fp-v1", "protected-invocation"))
        ]);
        var workflow = new ProtectedForkWorkflow();
        var authorizer = new ForkSequencedAuthorizer();
        var registry = new WorkflowRegistry().Register(new WorkflowRegistration<string, string>(
            new WorkflowDefinition { Name = WorkflowName, Version = "1" },
            () => workflow,
            manifest));
        await using var engine = new WorkflowEngine(store, registry, new ZhinuOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(10),
            LeaseDuration = TimeSpan.FromSeconds(2),
            LeaseRenewalInterval = TimeSpan.FromMilliseconds(600),
            ExecutionAuthorization = new WorkflowExecutionAuthorizationOptions("policy", "host-v1", authorizer)
        });
        var sourceId = await engine.StartAsync(WorkflowName, "1", "payload",
            authoritySubjectBinding: SubjectBinding, cancellationToken: Ct);
        await engine.ExecuteAsync(sourceId, Ct);
        (await engine.WaitForCompletionAsync<string>(sourceId, cancellationToken: Ct)).Should().Be("protected:payload");
        authorizer.Calls.Should().Be(1);

        var reuseForkId = await engine.ForkAsync(sourceId, "finish", new ForkRunOptions
        {
            Mode = StepRestartMode.StepOnly
        }, Ct);
        (await store.GetRunAsync(reuseForkId, Ct))!.AuthoritySubjectBinding.Should().Be(SubjectBinding);
        (await AuthorizationEvidenceCountAsync(reuseForkId)).Should().Be(0,
            "fork admission must not copy authorization evidence from the source");
        await engine.ExecuteAsync(reuseForkId, Ct);
        (await engine.WaitForCompletionAsync<string>(reuseForkId, cancellationToken: Ct)).Should().Be("protected:payload");
        workflow.ProtectedCallsFor(sourceId).Should().Be(1);
        workflow.ProtectedCallsFor(reuseForkId).Should().Be(0);
        (await AuthorizationEvidenceCountAsync(reuseForkId, allowed: true, stepKey: "protected")).Should().Be(0);
        authorizer.Calls.Should().Be(1, "the fork reused the protected step instead of carrying its authorization grant");

        var reexecuteForkId = await engine.ForkAsync(sourceId, "protected", new ForkRunOptions
        {
            Mode = StepRestartMode.StepOnly
        }, Ct);
        (await store.GetRunAsync(reexecuteForkId, Ct))!.AuthoritySubjectBinding.Should().Be(SubjectBinding);
        await engine.ExecuteAsync(reexecuteForkId, Ct);
        var failed = () => engine.WaitForCompletionAsync<string>(reexecuteForkId, cancellationToken: Ct);
        await failed.Should().ThrowAsync<WorkflowExecutionFailedException>();
        workflow.ProtectedCallsFor(sourceId).Should().Be(1);
        workflow.ProtectedCallsFor(reexecuteForkId).Should().Be(0);
        authorizer.Calls.Should().Be(2);
        (await AuthorizationEvidenceCountAsync(reexecuteForkId, allowed: true, stepKey: "protected")).Should().Be(0);
        (await store.GetRunAsync(sourceId, Ct))!.Status.Should().Be(WorkflowStatus.Completed);
    }

    private static WorkflowEngine CreateEngine(IWorkflowStore store, IWorkflowRegistry registry) =>
        new(store, registry, new ZhinuOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(10),
            LeaseDuration = TimeSpan.FromSeconds(2),
            LeaseRenewalInterval = TimeSpan.FromMilliseconds(600)
        });

    private async Task<int> AuthorizationEvidenceCountAsync(Guid runId, bool? allowed = null, string? stepKey = null)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(root, "zhinu.db"),
            Pooling = false
        }.ToString());
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        var predicates = new List<string> { "workflow_run_id=$run" };
        if (allowed is { } decision)
        {
            predicates.Add("decision=$decision");
            command.Parameters.AddWithValue("$decision", (int)(decision
                ? ExecutionAuthorizationDecision.Allowed
                : ExecutionAuthorizationDecision.Denied));
        }
        if (stepKey is not null)
        {
            predicates.Add("step_key=$step");
            command.Parameters.AddWithValue("$step", stepKey);
        }
        command.CommandText = $"SELECT COUNT(*) FROM workflow_authorization_evidence WHERE {string.Join(" AND ", predicates)};";
        command.Parameters.AddWithValue("$run", runId.ToString("D"));
        return Convert.ToInt32(await command.ExecuteScalarAsync(Ct));
    }

    private static WorkflowRegistry Registry(
        ForkProbe probe,
        ActivityGroupManifest sourceManifest,
        ActivityGroupManifest? targetManifest)
    {
        var registry = new WorkflowRegistry().Register(new WorkflowRegistration<string, string>(
            new WorkflowDefinition { Name = WorkflowName, Version = "1" },
            () => new ForkWorkflow(probe, version: 1),
            sourceManifest));
        var target = targetManifest is null
            ? new WorkflowRegistration<string, string>(
                new WorkflowDefinition { Name = WorkflowName, Version = "2" },
                () => new ForkWorkflow(probe, version: 2))
            : new WorkflowRegistration<string, string>(
                new WorkflowDefinition { Name = WorkflowName, Version = "2" },
                () => new ForkWorkflow(probe, version: 2),
                targetManifest);
        return registry.Register(target);
    }

    private static ActivityGroupManifest CreateManifest(
        string leftDisplay = "Left",
        string rightDisplay = "Right",
        int leftOrdinal = 0,
        int rightOrdinal = 1,
        string leftParent = "root",
        ActivityGroupMacroProvenance? leftProvenance = null,
        int alternateOrdinal = 2)
    {
        leftProvenance ??= new ActivityGroupMacroProvenance("pipeline", "fingerprint-v1", "invocation");
        var definitions = new List<ActivityGroupDefinition>
        {
            new("root", ActivityGroupKind.Logical, "Root"),
            new("left", ActivityGroupKind.Macro, leftDisplay, leftParent, leftOrdinal, leftProvenance),
            new("right", ActivityGroupKind.Logical, rightDisplay, "root", rightOrdinal)
        };
        if (leftParent == "alternate")
            definitions.Add(new ActivityGroupDefinition(
                "alternate", ActivityGroupKind.Logical, "Alternate", childOrdinal: alternateOrdinal));
        return ActivityGroupManifest.Create(definitions);
    }

    private static ActivityGroupOccurrence RootOccurrence(Guid runId, ActivityGroupManifest manifest) =>
        ActivityGroupOccurrence.Create(runId, manifest, "root", null, "[]");

    private async Task<RunSnapshot> CaptureRunAsync(IWorkflowStore store, Guid runId)
    {
        var run = await store.GetRunAsync(runId, Ct);
        var steps = await store.GetStepsAsync(runId, Ct);
        var memberships = new List<ActivityGroupStepMembership>();
        var occurrences = new Dictionary<string, OccurrenceSnapshot>(StringComparer.Ordinal);
        foreach (var step in steps.OrderBy(item => item.StepKey, StringComparer.Ordinal))
        {
            var membership = await ((IWorkflowGroupRepository)store).GetStepGroupMembershipAsync(step.Id, Ct);
            if (membership is null)
                continue;
            memberships.Add(membership);
            await CaptureOccurrenceTreeAsync(store, runId, membership.GroupOccurrenceId, occurrences);
        }
        var manifest = await ((IWorkflowGroupRepository)store).GetGroupManifestAsync(runId, Ct);
        return new RunSnapshot(
            run,
            manifest?.CanonicalJson,
            steps.OrderBy(item => item.StepKey, StringComparer.Ordinal).Select(item =>
                (item.Id, item.StepKey, item.Revision, item.Attempt, item.Status)).ToArray(),
            memberships.OrderBy(item => item.StepKey, StringComparer.Ordinal).ToArray(),
            occurrences.Values.OrderBy(item => item.GroupOccurrenceId, StringComparer.Ordinal).ToArray());
    }

    private async Task CaptureOccurrenceTreeAsync(
        IWorkflowStore store,
        Guid runId,
        string occurrenceId,
        Dictionary<string, OccurrenceSnapshot> occurrences)
    {
        if (occurrences.ContainsKey(occurrenceId))
            return;
        var occurrence = await ((IWorkflowGroupRepository)store).GetGroupOccurrenceAsync(runId, occurrenceId, Ct);
        occurrence.Should().NotBeNull();
        occurrences.Add(occurrenceId, new OccurrenceSnapshot(
            occurrence!.GroupOccurrenceId,
            occurrence.GroupKey,
            occurrence.ParentGroupOccurrenceId,
            occurrence.ExecutionCoordinate));
        if (occurrence.ParentGroupOccurrenceId is { } parentId)
            await CaptureOccurrenceTreeAsync(store, runId, parentId, occurrences);
    }

    private static async Task<IReadOnlyList<ActivityGroupStepMembership>> ReadMembershipsAsync(
        IWorkflowStore store,
        Guid runId)
    {
        var results = new List<ActivityGroupStepMembership>();
        foreach (var step in await store.GetStepsAsync(runId, Ct))
        {
            var membership = await ((IWorkflowGroupRepository)store).GetStepGroupMembershipAsync(step.Id, Ct);
            if (membership is not null)
                results.Add(membership);
        }
        return results;
    }

    private static async Task<ActivityGroupStepMembership?> MembershipAsync(
        IWorkflowStore store,
        Guid runId,
        string stepKey)
    {
        var step = (await store.GetStepsAsync(runId, Ct)).SingleOrDefault(item => item.StepKey == stepKey);
        return step is null
            ? null
            : await ((IWorkflowGroupRepository)store).GetStepGroupMembershipAsync(step.Id, Ct);
    }

    private static IWorkflowStore HideGroupForkCapability(SqliteWorkflowStore store)
    {
        var proxy = DispatchProxy.Create<IWorkflowGroupStoreWithoutFork, WorkflowGroupStoreProxy>();
        ((WorkflowGroupStoreProxy)(object)proxy).Target = store;
        return proxy;
    }

    public interface IWorkflowGroupStoreWithoutFork : IWorkflowStore, IWorkflowGroupRepository { }

    public class WorkflowGroupStoreProxy : DispatchProxy
    {
        public object Target { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            try
            {
                return targetMethod!.Invoke(Target, args);
            }
            catch (TargetInvocationException exception) when (exception.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
                throw;
            }
        }
    }

    private sealed record RunSnapshot(
        WorkflowRun? Run,
        string? Manifest,
        (Guid Id, string StepKey, int Revision, int Attempt, StepStatus Status)[] Steps,
        ActivityGroupStepMembership[] Memberships,
        OccurrenceSnapshot[] Occurrences);

    private sealed record OccurrenceSnapshot(
        string GroupOccurrenceId,
        string GroupKey,
        string? ParentGroupOccurrenceId,
        string ExecutionCoordinate);

    private sealed class ForkProbe
    {
        private readonly ConcurrentDictionary<(Guid RunId, string StepKey), int> calls = new();
        private readonly ConcurrentDictionary<(Guid RunId, string StepKey), ConcurrentQueue<WorkflowStepContext>> attempts = new();
        private int leftFailureConsumed;

        public bool FailRight { get; set; }
        public bool FailLeftFirstAttempt { get; set; }
        public Guid? BlockRightRunId { get; set; }
        public TaskCompletionSource<bool> RightEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ReleaseRight { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void RunStep(string key, WorkflowStepContext context)
        {
            calls.AddOrUpdate((context.WorkflowRunId, key), 1, (_, previous) => previous + 1);
            attempts.GetOrAdd((context.WorkflowRunId, key), _ => new ConcurrentQueue<WorkflowStepContext>())
                .Enqueue(context);
            if (key == "right" && FailRight)
                throw new InvalidOperationException("right branch blocked");
            if (key == "left" && FailLeftFirstAttempt && context.Attempt == 1 &&
                Interlocked.Exchange(ref leftFailureConsumed, 1) == 0)
                throw new InvalidOperationException("first left attempt fails");
        }

        public async Task RunRightStepAsync(WorkflowStepContext context, CancellationToken cancellationToken)
        {
            RunStep("right", context);
            if (context.WorkflowRunId == BlockRightRunId && context.Attempt == 1)
            {
                RightEntered.TrySetResult(true);
                await ReleaseRight.Task.ConfigureAwait(false);
            }
        }

        public int CallsFor(Guid runId, string stepKey) =>
            calls.TryGetValue((runId, stepKey), out var count) ? count : 0;

        public IReadOnlyList<WorkflowStepContext> AttemptsFor(Guid runId, string stepKey) =>
            attempts.TryGetValue((runId, stepKey), out var values) ? values.ToArray() : [];
    }

    private sealed class ForkWorkflow(ForkProbe probe, int version) : IWorkflow<string, string>
    {
        public async Task<string> RunAsync(WorkflowContext context, string input, CancellationToken cancellationToken)
        {
            var before = await context.StepAsync(
                "before", input,
                (value, step, _) =>
                {
                    probe.RunStep("before", step);
                    return Task.FromResult(value);
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);
            var root = context.GetActivityGroup("root");
            var leftOccurrence = context.GetActivityGroup("left", root);
            var rightOccurrence = context.GetActivityGroup("right", root);
            var leftOptions = new StepOptions
            {
                DependsOn = ["before"],
                Group = new ActivityGroupStepBinding(new ActivityGroupMembership("left"), leftOccurrence),
                Retry = new RetryPolicy { MaxAttempts = 2 }
            };
            var rightOptions = new StepOptions
            {
                DependsOn = ["before"],
                Group = new ActivityGroupStepBinding(new ActivityGroupMembership("right"), rightOccurrence),
                Retry = new RetryPolicy { MaxAttempts = 1 }
            };
            var leftInput = version == 1 ? before : $"v{version}:{before}";
            var left = await context.StepAsync(
                "left", leftInput,
                (value, step, _) =>
                {
                    probe.RunStep("left", step);
                    return Task.FromResult($"left:{value}");
                },
                leftOptions,
                cancellationToken).ConfigureAwait(false);
            var right = await context.StepAsync(
                "right", before,
                async (value, step, token) =>
                {
                    await probe.RunRightStepAsync(step, token).ConfigureAwait(false);
                    return $"right:{value}";
                },
                rightOptions,
                cancellationToken).ConfigureAwait(false);
            using (context.DependsOn("left", "right"))
            {
                return await context.StepAsync(
                    "join", $"{left}|{right}",
                    (value, step, _) =>
                    {
                        probe.RunStep("join", step);
                        return Task.FromResult(value);
                    },
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private sealed class PrefixForkWorkflow(ForkProbe probe, bool grouped) : IWorkflow<string, string>
    {
        public async Task<string> RunAsync(WorkflowContext context, string input, CancellationToken cancellationToken)
        {
            var before = await context.StepAsync(
                "before", input,
                (value, step, _) =>
                {
                    probe.RunStep("before", step);
                    return Task.FromResult(value);
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);
            var work = await context.StepAsync(
                "work", before,
                (value, step, _) =>
                {
                    probe.RunStep("work", step);
                    return Task.FromResult($"work:{value}");
                },
                new StepOptions { DependsOn = ["before"] },
                cancellationToken).ConfigureAwait(false);
            var finishInput = work;
            if (grouped)
            {
                var root = context.GetActivityGroup("root");
                finishInput = await context.StepAsync(
                    "added", work,
                    (value, step, _) =>
                    {
                        probe.RunStep("added", step);
                        return Task.FromResult(value);
                    },
                    new StepOptions
                    {
                        DependsOn = ["work"],
                        Group = new ActivityGroupStepBinding(
                            new ActivityGroupMembership("added"), context.GetActivityGroup("added", root))
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            using (context.DependsOn(grouped ? "added" : "work"))
            {
                return await context.StepAsync(
                    "finish", finishInput,
                    (value, step, _) =>
                    {
                        probe.RunStep("finish", step);
                        return Task.FromResult(value);
                    },
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private sealed class ProtectedForkWorkflow : IWorkflow<string, string>
    {
        private readonly ConcurrentDictionary<Guid, int> protectedCalls = new();

        public int ProtectedCallsFor(Guid runId) =>
            protectedCalls.TryGetValue(runId, out var count) ? count : 0;

        public async Task<string> RunAsync(WorkflowContext context, string input, CancellationToken cancellationToken)
        {
            var root = context.GetActivityGroup("root");
            var protectedOccurrence = context.GetActivityGroup("protected", root);
            var protectedResult = await context.StepAsync(
                "protected", input,
                (value, _) =>
                {
                    protectedCalls.AddOrUpdate(context.WorkflowRunId, 1, (_, count) => count + 1);
                    return Task.FromResult($"protected:{value}");
                },
                new StepOptions
                {
                    Group = new ActivityGroupStepBinding(new ActivityGroupMembership("protected"), protectedOccurrence),
                    Authorization = new WorkflowAuthorizationDeclaration([
                        new ExecutionRequirement("profile", 1, "execute", "protected")])
                },
                cancellationToken).ConfigureAwait(false);
            using (context.DependsOn("protected"))
            {
                return await context.StepAsync(
                    "finish", protectedResult,
                    (value, _) => Task.FromResult(value),
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private sealed class ForkSequencedAuthorizer : IExecutionAuthorizer
    {
        private int calls;
        public int Calls => Volatile.Read(ref calls);

        public ValueTask<ExecutionAuthorizationResult> AuthorizeAsync(
            ExecutionAuthorizationContext context,
            CancellationToken cancellationToken = default)
        {
            var hasRequirements = context.Requirements.Count > 0;
            var call = hasRequirements ? Interlocked.Increment(ref calls) : 0;
            return ValueTask.FromResult(new ExecutionAuthorizationResult(
                !hasRequirements || call == 1
                    ? ExecutionAuthorizationDecision.Allowed
                    : ExecutionAuthorizationDecision.Denied,
                context.AuthorizationRequestId,
                "policy",
                $"fork-auth-{(hasRequirements ? call : "empty")}",
                DateTimeOffset.UtcNow,
                !hasRequirements || call == 1 ? DateTimeOffset.UtcNow.AddMinutes(1) : null));
        }
    }
}
