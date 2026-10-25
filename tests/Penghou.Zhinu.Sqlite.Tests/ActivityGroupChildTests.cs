using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;

namespace Penghou.Zhinu.Sqlite.Tests;

public sealed class ActivityGroupChildTests(ITestOutputHelper testOutput) : WorkflowEngineTestBase
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public async Task ChildBoundariesAndChildWorkUseIndependentRunMemberships(
        bool childGrouped, bool startGrouped, bool waitGrouped)
    {
        var store = CreateStore();
        var parent = new ChildParentWorkflow(startGrouped, waitGrouped);
        var child = new ChildWorkflow(childGrouped);
        var registry = Registry(parent, child);
        await using var engine = new WorkflowEngine(store, registry);
        var parentId = await engine.StartAsync(ParentName, "1", "input", cancellationToken: Ct);

        await engine.ExecuteAsync(parentId, Ct);
        (await engine.WaitForCompletionAsync<string>(parentId, cancellationToken: Ct))
            .Should().Be("input:child");

        var childRun = (await engine.GetRunsAsync(new RunQuery { WorkflowName = ChildName }, Ct)).Single();
        childRun.ParentRunId.Should().Be(parentId);
        childRun.GroupManifestDigest.Should().Be(childGrouped ? Manifest.ManifestDigest : null);
        (await store.GetGroupManifestAsync(parentId, Ct))!.ManifestDigest.Should().Be(Manifest.ManifestDigest);
        if (childGrouped)
            (await store.GetGroupManifestAsync(childRun.Id, Ct))!.CanonicalJson.Should().Be(Manifest.CanonicalJson);
        else
            (await store.GetGroupManifestAsync(childRun.Id, Ct)).Should().BeNull();

        var parentSteps = await store.GetStepsAsync(parentId, Ct);
        var start = parentSteps.Single(step => step.StepKey == "child:start");
        var wait = parentSteps.Single(step => step.StepKey == "child:wait");
        var startMembership = await store.GetStepGroupMembershipAsync(start.Id, Ct);
        var waitMembership = await store.GetStepGroupMembershipAsync(wait.Id, Ct);
        if (!startGrouped)
            startMembership.Should().BeNull();
        if (!waitGrouped)
            waitMembership.Should().BeNull();
        if (startGrouped)
        {
            startMembership!.WorkflowRunId.Should().Be(parentId);
            startMembership.Role.Should().Be(ActivityGroupMemberRole.Control);
            startMembership.GroupOccurrenceId.Should().Be(
                parent.ParentLeafOccurrence!.GroupOccurrenceId);
        }
        if (waitGrouped)
        {
            waitMembership!.WorkflowRunId.Should().Be(parentId);
            waitMembership.Role.Should().Be(ActivityGroupMemberRole.Work);
            waitMembership.GroupOccurrenceId.Should().Be(
                parent.ParentLeafOccurrence!.GroupOccurrenceId);
        }

        var childStep = (await store.GetStepsAsync(childRun.Id, Ct)).Single(step => step.StepKey == "work");
        var childMembership = await store.GetStepGroupMembershipAsync(childStep.Id, Ct);
        if (!childGrouped)
            childMembership.Should().BeNull();
        if (childGrouped)
        {
            childMembership!.WorkflowRunId.Should().Be(childRun.Id);
            childMembership.Role.Should().Be(ActivityGroupMemberRole.Work);
            var childOccurrence = await store.GetGroupOccurrenceAsync(
                childRun.Id, childMembership.GroupOccurrenceId, Ct);
            childOccurrence!.GroupKey.Should().Be("leaf");
            childOccurrence.ParentGroupOccurrenceId.Should().NotBeNull();
            childOccurrence.ParentGroupOccurrenceId.Should().NotBe(
                parent.ParentLeafOccurrence!.ParentGroupOccurrenceId);
            childOccurrence.GroupOccurrenceId.Should().NotBe(parent.ParentLeafOccurrence.GroupOccurrenceId);
            (await store.GetGroupOccurrenceAsync(parentId, childOccurrence.GroupOccurrenceId, Ct)).Should().BeNull();
            (await store.GetGroupOccurrenceAsync(childRun.Id,
                parent.ParentLeafOccurrence.ParentGroupOccurrenceId!, Ct)).Should().BeNull();
        }

        var expectedChildId = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{parentId:D}:child:{start.Revision}")).AsSpan(0, 16));
        childRun.Id.Should().Be(expectedChildId);
        testOutput.WriteLine("CHILD_EXECUTION_EVIDENCE=" + JsonSerializer.Serialize(new
        {
            ParentRunId = parentId,
            ParentManifest = (await store.GetGroupManifestAsync(parentId, Ct))!.ManifestDigest,
            ChildRunId = childRun.Id,
            ChildManifest = childRun.GroupManifestDigest,
            ChildIdFormula = $"SHA256(parentRunId:{parentId:D}, child, startRevision:{start.Revision})[0..16]",
            ParentMemberships = new { Start = startMembership, Wait = waitMembership },
            ChildMembership = childMembership,
            ChildOccurrence = childGrouped
                ? await store.GetGroupOccurrenceAsync(childRun.Id, childMembership!.GroupOccurrenceId, Ct)
                : null
        }));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task BothBindingsAreValidatedBeforeEitherParentBoundaryIsClaimed(
        bool invalidStart, bool invalidWait)
    {
        var store = CreateStore();
        var firstParent = new ChildParentWorkflow(startGrouped: true, waitGrouped: true);
        var firstChild = new ChildWorkflow(grouped: true);
        await using var firstEngine = new WorkflowEngine(store, Registry(firstParent, firstChild));
        var firstId = await firstEngine.StartAsync(ParentName, "1", "first", cancellationToken: Ct);
        await firstEngine.ExecuteAsync(firstId, Ct);
        await firstEngine.WaitForCompletionAsync<string>(firstId, cancellationToken: Ct);
        var foreign = firstParent.ParentLeafOccurrence!;

        var invalidParent = new ChildParentWorkflow(
            startGrouped: true,
            waitGrouped: true,
            foreignBinding: foreign,
            invalidStart: invalidStart,
            invalidWait: invalidWait);
        var invalidChild = new ChildWorkflow(grouped: true);
        await using var invalidEngine = new WorkflowEngine(store, Registry(invalidParent, invalidChild));
        var invalidId = await invalidEngine.StartAsync(ParentName, "1", "invalid", cancellationToken: Ct);

        await invalidEngine.ExecuteAsync(invalidId, Ct);
        var completion = () => invalidEngine.WaitForCompletionAsync<string>(invalidId, cancellationToken: Ct);
        await completion.Should().ThrowAsync<WorkflowExecutionFailedException>();
        var invalidSteps = await store.GetStepsAsync(invalidId, Ct);
        invalidSteps.Should().BeEmpty();
        (await store.GetRunsAsync(new RunQuery { WorkflowName = ChildName }, Ct))
            .Should().NotContain(run => run.ParentRunId == invalidId);
        invalidChild.Calls.Should().Be(0);
        testOutput.WriteLine("CHILD_REJECTION_EVIDENCE=" + JsonSerializer.Serialize(new
        {
            InvalidStart = invalidStart,
            InvalidWait = invalidWait,
            ParentRunId = invalidId,
            ParentSteps = invalidSteps,
            ChildRuns = await store.GetRunsAsync(new RunQuery { WorkflowName = ChildName }, Ct),
            ChildCallbacks = invalidChild.Calls
        }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SameRunWrongManifestAndUntypedLoopScopeFailBeforeChildBoundaryClaims(bool loopScoped)
    {
        var store = CreateStore();
        var parent = new ChildParentWorkflow(
            startGrouped: true,
            waitGrouped: true,
            foreignManifest: loopScoped ? null : OtherManifest,
            invalidWait: !loopScoped,
            loopScopedWait: loopScoped);
        var child = new ChildWorkflow(grouped: true);
        await using var engine = new WorkflowEngine(store, Registry(parent, child));
        var parentId = await engine.StartAsync(ParentName, "1", "invalid", cancellationToken: Ct);

        await engine.ExecuteAsync(parentId, Ct);
        var completion = () => engine.WaitForCompletionAsync<string>(parentId, cancellationToken: Ct);
        var failure = await completion.Should().ThrowAsync<WorkflowExecutionFailedException>();
        failure.Which.Error.Message.Should().Contain(loopScoped
            ? "typed execution scope or an enclosing scope"
            : "occurrence from this run's admitted manifest");
        var steps = await store.GetStepsAsync(parentId, Ct);
        steps.Should().NotContain(step => step.StepKey == "child:start" || step.StepKey == "child:wait");
        (await store.GetRunsAsync(new RunQuery { WorkflowName = ChildName }, Ct))
            .Should().NotContain(run => run.ParentRunId == parentId);
        child.Calls.Should().Be(0);
        testOutput.WriteLine("CHILD_REJECTION_EVIDENCE=" + JsonSerializer.Serialize(new
        {
            LoopScoped = loopScoped,
            ParentRunId = parentId,
            ParentSteps = steps.Select(step => new { step.StepKey, step.Revision, step.Status }),
            ChildRuns = await store.GetRunsAsync(new RunQuery { WorkflowName = ChildName }, Ct),
            Failure = failure.Which.Error.Message
        }));
    }

    [Fact]
    public async Task GroupOptionsDoNotChangeSerializedChildStartRequestOrDeterministicChildId()
    {
        var store = CreateStore();
        var grouped = new ChildParentWorkflow(startGrouped: true, waitGrouped: true);
        var ungrouped = new ChildParentWorkflow(startGrouped: false, waitGrouped: false);
        var childA = new ChildWorkflow(grouped: true);
        var registry = new WorkflowRegistry()
            .Register(new WorkflowRegistration<string, string>(
                new WorkflowDefinition { Name = ParentName, Version = "1" }, () => grouped, Manifest))
            .Register(new WorkflowRegistration<string, string>(
                new WorkflowDefinition { Name = "group-child-parent-plain", Version = "1" }, () => ungrouped, Manifest))
            .Register(new WorkflowRegistration<string, string>(
                new WorkflowDefinition { Name = ChildName, Version = "1" }, () => childA, Manifest));
        await using var engine = new WorkflowEngine(store, registry);
        var groupedId = await engine.StartAsync(ParentName, "1", "same-input", cancellationToken: Ct);
        var plainId = await engine.StartAsync("group-child-parent-plain", "1", "same-input", cancellationToken: Ct);
        await engine.ExecuteAsync(groupedId, Ct);
        await engine.ExecuteAsync(plainId, Ct);

        var groupedStart = (await store.GetStepsAsync(groupedId, Ct)).Single(step => step.StepKey == "child:start");
        var plainStart = (await store.GetStepsAsync(plainId, Ct)).Single(step => step.StepKey == "child:start");
        groupedStart.InputJson.Should().Be(plainStart.InputJson);
        var groupedChild = (await store.GetRunsAsync(new RunQuery { WorkflowName = ChildName }, Ct))
            .Single(run => run.ParentRunId == groupedId);
        var plainChild = (await store.GetRunsAsync(new RunQuery { WorkflowName = ChildName }, Ct))
            .Single(run => run.ParentRunId == plainId);
        groupedChild.Id.Should().Be(ChildId(groupedId, groupedStart.Revision));
        plainChild.Id.Should().Be(ChildId(plainId, plainStart.Revision));
        groupedChild.Id.Should().NotBe(plainChild.Id);
    }

    [Fact]
    public async Task RestartingWaitReusesChildAndRestartingStartCreatesFreshChildWithMemberships()
    {
        var store = CreateStore();
        var parent = new ChildParentWorkflow(startGrouped: true, waitGrouped: true);
        var child = new ChildWorkflow(grouped: true);
        await using var engine = new WorkflowEngine(store, Registry(parent, child));
        var parentId = await engine.StartAsync(ParentName, "1", "input", cancellationToken: Ct);
        await engine.ExecuteAsync(parentId, Ct);
        await engine.WaitForCompletionAsync<string>(parentId, cancellationToken: Ct);
        var originalChild = (await store.GetRunsAsync(new RunQuery { WorkflowName = ChildName }, Ct)).Single();
        var originalWait = (await store.GetStepsAsync(parentId, Ct)).Single(step => step.StepKey == "child:wait");
        var originalWaitMembership = await store.GetStepGroupMembershipAsync(originalWait.Id, Ct);
        var originalStart = (await store.GetStepsAsync(parentId, Ct)).Single(step => step.StepKey == "child:start");
        var originalStartMembership = await store.GetStepGroupMembershipAsync(originalStart.Id, Ct);

        await engine.RestartStepAsync(parentId, "child:wait",
            new RestartStepOptions { Mode = StepRestartMode.StepOnly }, Ct);
        await engine.ExecuteAsync(parentId, Ct);
        await engine.WaitForCompletionAsync<string>(parentId, cancellationToken: Ct);
        var afterWaitRestart = (await store.GetRunsAsync(new RunQuery { WorkflowName = ChildName }, Ct)).Single();
        afterWaitRestart.Id.Should().Be(originalChild.Id);
        var waitAfterRestart = (await store.GetStepsAsync(parentId, Ct)).Single(step => step.StepKey == "child:wait");
        var waitMembershipAfterRestart = await store.GetStepGroupMembershipAsync(waitAfterRestart.Id, Ct);
        waitMembershipAfterRestart!.GroupOccurrenceId.Should().Be(originalWaitMembership!.GroupOccurrenceId);
        waitAfterRestart.Revision.Should().Be(originalWait.Revision + 1);
        (await store.GetStepGroupMembershipAsync(originalWait.Id, Ct)).Should().BeEquivalentTo(originalWaitMembership);
        (await store.GetStepGroupMembershipAsync(originalStart.Id, Ct)).Should().BeEquivalentTo(originalStartMembership);

        await engine.RestartStepAsync(parentId, "child:start", Ct);
        await engine.ExecuteAsync(parentId, Ct);
        await engine.WaitForCompletionAsync<string>(parentId, cancellationToken: Ct);
        var children = await store.GetRunsAsync(new RunQuery { WorkflowName = ChildName }, Ct);
        children.Should().HaveCount(2);
        var newChild = children.Single(run => run.Id != originalChild.Id);
        var startAfterRestart = (await store.GetStepsAsync(parentId, Ct)).Single(step => step.StepKey == "child:start");
        newChild.Id.Should().Be(ChildId(parentId, startAfterRestart.Revision));
        startAfterRestart.Revision.Should().Be(originalStart.Revision + 1);
        var newStartMembership = await store.GetStepGroupMembershipAsync(startAfterRestart.Id, Ct);
        newStartMembership.Should().NotBeNull();
        newStartMembership!.GroupOccurrenceId.Should().Be(parent.ParentLeafOccurrence!.GroupOccurrenceId);
        var newWait = (await store.GetStepsAsync(parentId, Ct)).Single(step => step.StepKey == "child:wait");
        (await store.GetStepGroupMembershipAsync(newWait.Id, Ct))!.GroupOccurrenceId
            .Should().Be(parent.ParentLeafOccurrence.GroupOccurrenceId);
        child.Calls.Should().Be(2);
        (await store.GetStepGroupMembershipAsync(originalStart.Id, Ct)).Should().BeEquivalentTo(originalStartMembership);
        (await store.GetStepGroupMembershipAsync(originalWait.Id, Ct)).Should().BeEquivalentTo(originalWaitMembership);
        testOutput.WriteLine("CHILD_RESTART_EVIDENCE=" + JsonSerializer.Serialize(new
        {
            ParentRunId = parentId,
            OriginalChildId = originalChild.Id,
            FinalChildIds = children.Select(run => run.Id),
            OriginalStart = new { originalStart.Id, originalStart.Revision, Membership = originalStartMembership },
            FinalStart = new { startAfterRestart.Id, startAfterRestart.Revision, Membership = newStartMembership },
            OriginalWait = new { originalWait.Id, originalWait.Revision, Membership = originalWaitMembership },
            FinalWait = new { newWait.Id, newWait.Revision, Membership = await store.GetStepGroupMembershipAsync(newWait.Id, Ct) },
            ChildCallbacks = child.Calls
        }));
    }

    [Fact]
    public async Task ChildStepRetryReusesTheSameOccurrenceAcrossAttempts()
    {
        var store = CreateStore();
        var parent = new ChildParentWorkflow(startGrouped: true, waitGrouped: true);
        var child = new ChildWorkflow(grouped: true, failFirstAttempt: true);
        await using var engine = new WorkflowEngine(store, Registry(parent, child));
        var parentId = await engine.StartAsync(ParentName, "1", "retry", cancellationToken: Ct);
        await engine.ExecuteAsync(parentId, Ct);
        (await engine.WaitForCompletionAsync<string>(parentId, cancellationToken: Ct)).Should().Be("retry:child");

        var childRun = (await store.GetRunsAsync(new RunQuery { WorkflowName = ChildName }, Ct)).Single();
        var step = (await store.GetStepsAsync(childRun.Id, Ct)).Single(item => item.StepKey == "work");
        step.Attempt.Should().Be(2);
        child.Calls.Should().Be(2);
        var membership = await store.GetStepGroupMembershipAsync(step.Id, Ct);
        membership.Should().NotBeNull();
        var occurrence = await store.GetGroupOccurrenceAsync(childRun.Id, membership!.GroupOccurrenceId, Ct);
        occurrence!.WorkflowRunId.Should().Be(childRun.Id);
        occurrence.ExecutionCoordinate.Should().Be("[]");
        testOutput.WriteLine("CHILD_RETRY_EVIDENCE=" + JsonSerializer.Serialize(new
        {
            ParentRunId = parentId,
            ChildRunId = childRun.Id,
            ChildStep = new { step.Id, step.Revision, step.Attempt, step.Status },
            Membership = membership,
            Occurrence = occurrence,
            Calls = child.Calls
        }));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReplayingChangedOrRemovedGroupBindingRejectsWithoutChangingStepHistory(bool driftStart)
    {
        var store = CreateStore();
        var parent = new ChildParentDriftWorkflow(store, driftStart);
        var child = new ChildWorkflow(grouped: true);
        var registry = new WorkflowRegistry()
            .Register(new WorkflowRegistration<string, string>(
                new WorkflowDefinition { Name = ParentName, Version = "1" }, () => parent, Manifest))
            .Register(new WorkflowRegistration<string, string>(
                new WorkflowDefinition { Name = ChildName, Version = "1" }, () => child, Manifest));
        await using var engine = new WorkflowEngine(store, registry);
        var parentId = await engine.StartAsync(ParentName, "1", "drift", cancellationToken: Ct);
        await engine.ExecuteAsync(parentId, Ct);
        var completion = () => engine.WaitForCompletionAsync<string>(parentId, cancellationToken: Ct);
        var failure = await completion.Should().ThrowAsync<WorkflowExecutionFailedException>();
        failure.Which.Error.Message.Should().Contain(driftStart
            ? "grouped but this claim is ungrouped"
            : "different group membership");
        var after = await store.GetStepsAsync(parentId, Ct);
        after.Select(StepIdentity).Should().BeEquivalentTo(parent.BeforeReplaySteps!.Select(StepIdentity));
        foreach (var step in after)
            (await store.GetStepGroupMembershipAsync(step.Id, Ct)).Should().BeEquivalentTo(
                parent.BeforeReplayMemberships![step.Id]);
        (await store.GetRunsAsync(new RunQuery { WorkflowName = ChildName }, Ct)).Should().ContainSingle();
        child.Calls.Should().Be(1);
        testOutput.WriteLine("CHILD_REJECTION_EVIDENCE=" + JsonSerializer.Serialize(new
        {
            DriftStart = driftStart,
            ParentRunId = parentId,
            Before = parent.BeforeReplaySteps!.Select(StepIdentity),
            After = after.Select(StepIdentity),
            Memberships = parent.BeforeReplayMemberships,
            Failure = failure.Which.Error.Message,
            ChildRuns = await store.GetRunsAsync(new RunQuery { WorkflowName = ChildName }, Ct)
        }));
    }

    [Fact]
    public async Task ForkingAtWaitCopiesStartLineageAndRebindsWaitToDestinationRun()
    {
        var store = CreateStore();
        var parent = new ChildParentWorkflow(startGrouped: true, waitGrouped: true);
        var child = new ChildWorkflow(grouped: true);
        await using var engine = new WorkflowEngine(store, Registry(parent, child));
        var sourceId = await engine.StartAsync(ParentName, "1", "input", cancellationToken: Ct);
        await engine.ExecuteAsync(sourceId, Ct);
        await engine.WaitForCompletionAsync<string>(sourceId, cancellationToken: Ct);
        var sourceSteps = await store.GetStepsAsync(sourceId, Ct);
        var sourceStart = sourceSteps.Single(step => step.StepKey == "child:start");
        var sourceWait = sourceSteps.Single(step => step.StepKey == "child:wait");
        var sourceStartMembership = await store.GetStepGroupMembershipAsync(sourceStart.Id, Ct);
        var sourceWaitMembership = await store.GetStepGroupMembershipAsync(sourceWait.Id, Ct);
        var sourceChild = (await store.GetRunsAsync(new RunQuery { WorkflowName = ChildName }, Ct)).Single();

        var destinationId = await engine.ForkAsync(sourceId, "child:wait", new ForkRunOptions
        {
            WorkflowRunId = Guid.NewGuid(),
            Mode = StepRestartMode.StepOnly
        }, Ct);
        await engine.ExecuteAsync(destinationId, Ct);
        (await engine.WaitForCompletionAsync<string>(destinationId, cancellationToken: Ct)).Should().Be("input:child");

        var destinationSteps = await store.GetStepsAsync(destinationId, Ct);
        var destinationStart = destinationSteps.Single(step => step.StepKey == "child:start");
        var destinationWait = destinationSteps.Single(step => step.StepKey == "child:wait");
        var destinationStartMembership = await store.GetStepGroupMembershipAsync(destinationStart.Id, Ct);
        var destinationWaitMembership = await store.GetStepGroupMembershipAsync(destinationWait.Id, Ct);
        destinationStartMembership!.WorkflowRunId.Should().Be(destinationId);
        destinationStartMembership.GroupOccurrenceId.Should().NotBe(sourceStartMembership!.GroupOccurrenceId);
        var sourceStartOccurrence = await store.GetGroupOccurrenceAsync(
            sourceId, sourceStartMembership.GroupOccurrenceId, Ct);
        var destinationStartOccurrence = await store.GetGroupOccurrenceAsync(
            destinationId, destinationStartMembership.GroupOccurrenceId, Ct);
        destinationStartOccurrence!.ExecutionCoordinate.Should().Be(sourceStartOccurrence!.ExecutionCoordinate);
        destinationWaitMembership!.WorkflowRunId.Should().Be(destinationId);
        destinationWaitMembership.GroupOccurrenceId.Should().NotBe(sourceWaitMembership!.GroupOccurrenceId);
        destinationWaitMembership.GroupOccurrenceId.Should().Be(parent.ParentLeafOccurrence!.GroupOccurrenceId);
        (await store.GetStepsAsync(sourceId, Ct)).Single(step => step.StepKey == "child:start").Id
            .Should().Be(sourceStart.Id);
        (await store.GetStepGroupMembershipAsync(sourceStart.Id, Ct)).Should().BeEquivalentTo(sourceStartMembership);
        (await store.GetStepGroupMembershipAsync(sourceWait.Id, Ct)).Should().BeEquivalentTo(sourceWaitMembership);
        sourceChild.ParentRunId.Should().Be(sourceId);
        (await store.GetRunsAsync(new RunQuery { WorkflowName = ChildName }, Ct)).Should().ContainSingle();
        child.Calls.Should().Be(1, "the fork reuses the completed child result through child:start");
        testOutput.WriteLine("CHILD_FORK_EVIDENCE=" + JsonSerializer.Serialize(new
        {
            SourceRunId = sourceId,
            DestinationRunId = destinationId,
            SourceChildId = sourceChild.Id,
            ReusedStart = new { destinationStart.Id, destinationStart.Revision, Membership = destinationStartMembership },
            ReexecutedWait = new { destinationWait.Id, destinationWait.Revision, Membership = destinationWaitMembership },
            SourceStartMembership = sourceStartMembership,
            SourceWaitMembership = sourceWaitMembership,
            ChildCallbacks = child.Calls
        }));
    }

    [Fact]
    public async Task ForkingAtChildStartCreatesDestinationOwnedChildAndTree()
    {
        var store = CreateStore();
        var parent = new ChildParentWorkflow(startGrouped: true, waitGrouped: true);
        var child = new ChildWorkflow(grouped: true);
        await using var engine = new WorkflowEngine(store, Registry(parent, child));
        var sourceId = await engine.StartAsync(ParentName, "1", "fork-start", cancellationToken: Ct);
        await engine.ExecuteAsync(sourceId, Ct);
        await engine.WaitForCompletionAsync<string>(sourceId, cancellationToken: Ct);
        var sourceChild = (await store.GetRunsAsync(new RunQuery { WorkflowName = ChildName }, Ct)).Single();
        var sourceChildStep = (await store.GetStepsAsync(sourceChild.Id, Ct)).Single(step => step.StepKey == "work");
        var sourceChildMembership = await store.GetStepGroupMembershipAsync(sourceChildStep.Id, Ct);
        var sourceStepsBefore = await store.GetStepsAsync(sourceId, Ct);
        var sourceMembershipsBefore = await MembershipsAsync(store, sourceStepsBefore, Ct);

        var destinationId = await engine.ForkAsync(sourceId, "child:start", new ForkRunOptions
        {
            WorkflowRunId = Guid.NewGuid(),
            Mode = StepRestartMode.StepOnly
        }, Ct);
        await engine.ExecuteAsync(destinationId, Ct);
        (await engine.WaitForCompletionAsync<string>(destinationId, cancellationToken: Ct)).Should().Be("fork-start:child");

        var children = await store.GetRunsAsync(new RunQuery { WorkflowName = ChildName }, Ct);
        children.Should().HaveCount(2);
        var destinationChild = children.Single(run => run.ParentRunId == destinationId);
        destinationChild.Id.Should().Be(ChildId(destinationId,
            (await store.GetStepsAsync(destinationId, Ct)).Single(step => step.StepKey == "child:start").Revision));
        destinationChild.Id.Should().NotBe(sourceChild.Id);
        destinationChild.GroupManifestDigest.Should().Be(Manifest.ManifestDigest);
        var destinationChildStep = (await store.GetStepsAsync(destinationChild.Id, Ct)).Single(step => step.StepKey == "work");
        var destinationChildMembership = await store.GetStepGroupMembershipAsync(destinationChildStep.Id, Ct);
        destinationChildMembership.Should().NotBeNull();
        destinationChildMembership!.WorkflowRunId.Should().Be(destinationChild.Id);
        destinationChildMembership.GroupOccurrenceId.Should().NotBe(sourceChildMembership!.GroupOccurrenceId);
        var destinationChildOccurrence = await store.GetGroupOccurrenceAsync(
            destinationChild.Id, destinationChildMembership.GroupOccurrenceId, Ct);
        destinationChildOccurrence!.GroupKey.Should().Be("leaf");
        destinationChildOccurrence.ParentGroupOccurrenceId.Should().NotBeNull();
        var destinationChildRoot = await store.GetGroupOccurrenceAsync(
            destinationChild.Id, destinationChildOccurrence.ParentGroupOccurrenceId!, Ct);
        destinationChildRoot!.WorkflowRunId.Should().Be(destinationChild.Id);
        destinationChildRoot.GroupKey.Should().Be("root");
        (await store.GetGroupOccurrenceAsync(
            sourceChild.Id, sourceChildMembership.GroupOccurrenceId, Ct))!.GroupOccurrenceId
            .Should().Be(sourceChildMembership.GroupOccurrenceId);
        (await store.GetGroupOccurrenceAsync(
            destinationChild.Id, sourceChildMembership.GroupOccurrenceId, Ct)).Should().BeNull();
        (await store.GetStepsAsync(sourceId, Ct)).Select(StepIdentity)
            .Should().BeEquivalentTo(sourceStepsBefore.Select(StepIdentity));
        var sourceMembershipsAfter = await MembershipsAsync(store,
            await store.GetStepsAsync(sourceId, Ct), Ct);
        sourceMembershipsAfter.Should().BeEquivalentTo(sourceMembershipsBefore);
        (await store.GetRunAsync(sourceChild.Id, Ct))!.ParentRunId.Should().Be(sourceId);
        child.Calls.Should().Be(2);
        testOutput.WriteLine("CHILD_FORK_EVIDENCE=" + JsonSerializer.Serialize(new
        {
            SourceRunId = sourceId,
            DestinationRunId = destinationId,
            SourceChildId = sourceChild.Id,
            DestinationChildId = destinationChild.Id,
            SourceChildMembership = sourceChildMembership,
            DestinationChildMembership = destinationChildMembership,
            DestinationChildOccurrence = destinationChildOccurrence,
            DestinationChildRoot = destinationChildRoot,
            DestinationChildCallbacks = child.Calls,
            SourceParentSteps = sourceStepsBefore.Select(StepIdentity),
            SourceParentMemberships = sourceMembershipsBefore
        }));
    }

    [Fact]
    public async Task ChildFailureKeepsBoundaryAndChildMembershipsAndFailsParentNormally()
    {
        var store = CreateStore();
        var parent = new ChildParentWorkflow(startGrouped: true, waitGrouped: true);
        var child = new ChildWorkflow(grouped: true, fail: true);
        await using var engine = new WorkflowEngine(store, Registry(parent, child));
        var parentId = await engine.StartAsync(ParentName, "1", "input", cancellationToken: Ct);
        await engine.ExecuteAsync(parentId, Ct);

        var completion = () => engine.WaitForCompletionAsync<string>(parentId, cancellationToken: Ct);
        (await completion.Should().ThrowAsync<WorkflowExecutionFailedException>())
            .Which.Error.Message.Should().Contain("child failed");
        var childRun = (await store.GetRunsAsync(new RunQuery { WorkflowName = ChildName }, Ct)).Single();
        childRun.Status.Should().Be(WorkflowStatus.Failed);
        foreach (var runId in new[] { parentId, childRun.Id })
        {
            var steps = await store.GetStepsAsync(runId, Ct);
            steps.Should().NotBeEmpty();
            foreach (var step in steps)
                (await store.GetStepGroupMembershipAsync(step.Id, Ct)).Should().NotBeNull();
        }
    }

    [Fact]
    public async Task UngroupedParentCanStartGroupedChildWithChildLocalTree()
    {
        var store = CreateStore();
        var child = new ChildWorkflow(grouped: true);
        var registry = new WorkflowRegistry()
            .Register(ParentName, "1", new UngroupedChildParentWorkflow())
            .Register(new WorkflowRegistration<string, string>(
                new WorkflowDefinition { Name = ChildName, Version = "1" }, () => child, Manifest));
        await using var engine = new WorkflowEngine(store, registry);
        var parentId = await engine.StartAsync(ParentName, "1", "plain-parent", cancellationToken: Ct);
        await engine.ExecuteAsync(parentId, Ct);
        (await engine.WaitForCompletionAsync<string>(parentId, cancellationToken: Ct)).Should().Be("plain-parent:child");

        var parentRun = (await store.GetRunAsync(parentId, Ct))!;
        parentRun.GroupManifestDigest.Should().BeNull();
        (await store.GetGroupManifestAsync(parentId, Ct)).Should().BeNull();
        var childRun = (await store.GetRunsAsync(new RunQuery { WorkflowName = ChildName }, Ct)).Single();
        childRun.ParentRunId.Should().Be(parentId);
        var childStep = (await store.GetStepsAsync(childRun.Id, Ct)).Single(step => step.StepKey == "work");
        var childMembership = await store.GetStepGroupMembershipAsync(childStep.Id, Ct);
        childMembership.Should().NotBeNull();
        var childOccurrence = await store.GetGroupOccurrenceAsync(childRun.Id, childMembership!.GroupOccurrenceId, Ct);
        childOccurrence!.WorkflowRunId.Should().Be(childRun.Id);
        (await store.GetGroupOccurrenceAsync(parentId, childOccurrence.GroupOccurrenceId, Ct)).Should().BeNull();
        var childRoot = await store.GetGroupOccurrenceAsync(childRun.Id, childOccurrence.ParentGroupOccurrenceId!, Ct);
        childRoot!.WorkflowRunId.Should().Be(childRun.Id);
        childRoot.GroupKey.Should().Be("root");
        testOutput.WriteLine("CHILD_EXECUTION_EVIDENCE=" + JsonSerializer.Serialize(new
        {
            ParentRun = parentRun,
            ChildRun = childRun,
            ChildMembership = childMembership,
            ChildOccurrence = childOccurrence,
            ChildRoot = childRoot,
            ParentLookupOfChildOccurrence = await store.GetGroupOccurrenceAsync(parentId, childOccurrence.GroupOccurrenceId, Ct)
        }));
    }

    private sealed class UngroupedChildParentWorkflow : IWorkflow<string, string>
    {
        public async Task<string> RunAsync(WorkflowContext context, string input, CancellationToken cancellationToken)
        {
            var result = await context.StartChildAsync<string, string>(
                "child", ChildName, "1", input, cancellationToken);
            return await context.StepAsync("tail", result,
                (_, _, _) => Task.FromResult(result), cancellationToken: cancellationToken);
        }
    }

    private static Guid ChildId(Guid parentRunId, int revision) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes($"{parentRunId:D}:child:{revision}")).AsSpan(0, 16));

    private static object StepIdentity(WorkflowStepRun step) => new
    {
        step.Id,
        step.StepKey,
        step.Revision,
        step.Status,
        step.Attempt,
        step.InputHash
    };

    private static async Task<Dictionary<Guid, ActivityGroupStepMembership?>> MembershipsAsync(
        SqliteWorkflowStore store, IReadOnlyCollection<WorkflowStepRun> steps, CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, ActivityGroupStepMembership?>();
        foreach (var step in steps)
            result[step.Id] = await store.GetStepGroupMembershipAsync(step.Id, cancellationToken);
        return result;
    }

    private static WorkflowRegistry Registry(ChildParentWorkflow parent, ChildWorkflow child)
    {
        var registry = new WorkflowRegistry()
            .Register(new WorkflowRegistration<string, string>(
                new WorkflowDefinition { Name = ParentName, Version = "1" }, () => parent, Manifest));
        if (child.Grouped)
            registry.Register(new WorkflowRegistration<string, string>(
                new WorkflowDefinition { Name = ChildName, Version = "1" }, () => child, Manifest));
        else
            registry.Register(ChildName, "1", child);
        return registry;
    }

    private const string ParentName = "group-child-parent";
    private const string ChildName = "group-child-leaf";
    private static ActivityGroupManifest Manifest => ActivityGroupManifest.Create(
    [
        new ActivityGroupDefinition("root", ActivityGroupKind.Logical, "Root"),
        new ActivityGroupDefinition("leaf", ActivityGroupKind.Macro, "Leaf", "root", 0,
            new ActivityGroupMacroProvenance("macro", "fingerprint", "child-invocation"))
    ]);
    private static ActivityGroupManifest OtherManifest => ActivityGroupManifest.Create(
    [
        new ActivityGroupDefinition("root", ActivityGroupKind.Logical, "Other root"),
        new ActivityGroupDefinition("leaf", ActivityGroupKind.Macro, "Other leaf", "root", 0,
            new ActivityGroupMacroProvenance("macro", "other-fingerprint", "other-invocation"))
    ]);

    private sealed class ChildParentWorkflow(
        bool startGrouped,
        bool waitGrouped,
        ActivityGroupOccurrence? foreignBinding = null,
        bool invalidStart = false,
        bool invalidWait = false,
        ActivityGroupManifest? foreignManifest = null,
        bool loopScopedWait = false) : IWorkflow<string, string>
    {
        public ActivityGroupOccurrence? ParentLeafOccurrence { get; private set; }

        public async Task<string> RunAsync(WorkflowContext context, string input, CancellationToken cancellationToken)
        {
            var root = context.GetActivityGroup("root");
            ParentLeafOccurrence = context.GetActivityGroup("leaf", root);
            var start = new ActivityGroupStepBinding(
                new ActivityGroupMembership("leaf", ActivityGroupMemberRole.Control),
                invalidStart ? foreignBinding! : ParentLeafOccurrence);
            var manifestLeaf = foreignManifest is not null
                ? ActivityGroupOccurrence.Create(
                    context.WorkflowRunId,
                    foreignManifest,
                    "leaf",
                    ActivityGroupOccurrence.Create(context.WorkflowRunId, foreignManifest, "root", null, "[]"),
                    "[]")
                : null;
            var waitOccurrence = invalidWait && foreignManifest is not null
                ? manifestLeaf!
                : invalidWait ? foreignBinding! : ParentLeafOccurrence;
            var wait = new ActivityGroupStepBinding(
                new ActivityGroupMembership("leaf", ActivityGroupMemberRole.Work),
                waitOccurrence);
            if (loopScopedWait)
            {
                await context.LoopAsync(
                    "child-scope",
                    0,
                    state => state < 1,
                    async (iteration, token) =>
                    {
                        var iterationLeaf = iteration.GetActivityGroup("leaf", root);
                        await context.StartChildAsync<string, string>(
                            "child", ChildName, "1", input,
                            new ChildRunOptions
                            {
                                StartGroup = startGrouped ? start : null,
                                WaitGroup = new ActivityGroupStepBinding(
                                    new ActivityGroupMembership("leaf", ActivityGroupMemberRole.Work), iterationLeaf)
                            },
                            token);
                        return iteration.Break(iteration.State + 1);
                    },
                    new LoopOptions(maxIterations: 1),
                    cancellationToken);
                return "loop";
            }
            var result = await context.StartChildAsync<string, string>(
                "child", ChildName, "1", input,
                new ChildRunOptions
                {
                    StartGroup = startGrouped ? start : null,
                    WaitGroup = waitGrouped ? wait : null
                },
                cancellationToken);
            return await context.StepAsync("tail", result,
                (_, _, _) => Task.FromResult(result), cancellationToken: cancellationToken);
        }
    }

    private sealed class ChildWorkflow(bool grouped, bool fail = false, bool failFirstAttempt = false) : IWorkflow<string, string>
    {
        public bool Grouped => grouped;
        public int Calls { get; private set; }

        public async Task<string> RunAsync(WorkflowContext context, string input, CancellationToken cancellationToken)
        {
            ActivityGroupStepBinding? binding = null;
            if (grouped)
            {
                var root = context.GetActivityGroup("root");
                var leaf = context.GetActivityGroup("leaf", root);
                binding = new ActivityGroupStepBinding(new ActivityGroupMembership("leaf"), leaf);
            }
            return await context.StepAsync("work", input,
                (_, _, _) =>
                {
                    Calls++;
                    if (fail || (failFirstAttempt && Calls == 1))
                        throw new InvalidOperationException("child failed");
                    return Task.FromResult(input + ":child");
                },
                binding is null ? null : new StepOptions
                {
                    Group = binding,
                    Retry = new RetryPolicy { MaxAttempts = failFirstAttempt ? 2 : 1 }
                },
                cancellationToken);
        }
    }

    private sealed class ChildParentDriftWorkflow(SqliteWorkflowStore store, bool driftStart) : IWorkflow<string, string>
    {
        public IReadOnlyCollection<WorkflowStepRun>? BeforeReplaySteps { get; private set; }
        public IReadOnlyDictionary<Guid, ActivityGroupStepMembership?>? BeforeReplayMemberships { get; private set; }

        public async Task<string> RunAsync(WorkflowContext context, string input, CancellationToken cancellationToken)
        {
            var root = context.GetActivityGroup("root");
            var leaf = context.GetActivityGroup("leaf", root);
            var start = new ActivityGroupStepBinding(
                new ActivityGroupMembership("leaf", ActivityGroupMemberRole.Control), leaf);
            var wait = new ActivityGroupStepBinding(
                new ActivityGroupMembership("leaf", ActivityGroupMemberRole.Work), leaf);
            await context.StartChildAsync<string, string>("child", ChildName, "1", input,
                new ChildRunOptions { StartGroup = start, WaitGroup = wait }, cancellationToken);
            BeforeReplaySteps = await store.GetStepsAsync(context.WorkflowRunId, cancellationToken);
            BeforeReplayMemberships = await MembershipsAsync(store, BeforeReplaySteps, cancellationToken);

            await context.StartChildAsync<string, string>("child", ChildName, "1", input,
                new ChildRunOptions
                {
                    StartGroup = driftStart ? null : start,
                    WaitGroup = driftStart ? wait : new ActivityGroupStepBinding(
                        new ActivityGroupMembership("leaf", ActivityGroupMemberRole.Control), leaf)
                }, cancellationToken);
            return "unexpected";
        }
    }

}
