using System.Collections.Concurrent;
using System.Text.Json;
using FluentAssertions;
using Penghou.Workflow.Abstractions;

namespace Penghou.Zhinu.Sqlite.Tests;

public sealed class ActivityGroupChildAuthorizationTests(ITestOutputHelper testOutput) : WorkflowEngineTestBase
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task GroupedChildBoundaries_AuthorizeStartWaitAndChildWorkIndependently()
    {
        var authorizer = new RecordingAuthorizer();
        var store = CreateStore();
        var parent = new GroupedParentWorkflow();
        var child = new GroupedChildWorkflow();
        var registry = Registry(parent, child);
        await using var engine = Engine(store, registry, authorizer);

        var parentId = await engine.StartAsync(ParentName, "1", "input", cancellationToken: Ct);
        await engine.ExecuteAsync(parentId, Ct);
        (await engine.WaitForCompletionAsync<string>(parentId, cancellationToken: Ct)).Should().Be("child:input");

        var childRun = (await store.GetRunSubtreeAsync(parentId, 5, Ct))
            .Single(run => run.Id != parentId);
        childRun.Status.Should().Be(WorkflowStatus.Completed);
        childRun.ParentRunId.Should().Be(parentId);
        child.Calls.Should().Be(1);

        var parentManifest = await store.GetGroupManifestAsync(parentId, Ct);
        var childManifest = await store.GetGroupManifestAsync(childRun.Id, Ct);
        parentManifest.Should().NotBeNull();
        childManifest!.CanonicalJson.Should().Be(GroupedChildWorkflow.Manifest.CanonicalJson);
        childManifest.CanonicalJson.Should().NotBe(parentManifest!.CanonicalJson);

        var parentSteps = await store.GetStepsAsync(parentId, Ct);
        var startStep = parentSteps.Single(step => step.StepKey == "child:start");
        var waitStep = parentSteps.Single(step => step.StepKey == "child:wait");
        var startMembership = (await store.GetStepGroupMembershipAsync(startStep.Id, Ct))!;
        var waitMembership = (await store.GetStepGroupMembershipAsync(waitStep.Id, Ct))!;
        startMembership.Role.Should().Be(ActivityGroupMemberRole.Control);
        waitMembership.Role.Should().Be(ActivityGroupMemberRole.Work);
        startMembership.GroupOccurrenceId.Should().Be(waitMembership.GroupOccurrenceId);
        var parentOccurrence = await store.GetGroupOccurrenceAsync(parentId, startMembership.GroupOccurrenceId, Ct);
        parentOccurrence.Should().NotBeNull();
        parentOccurrence!.GroupKey.Should().Be("invocation");
        parentOccurrence.ParentGroupOccurrenceId.Should().BeNull();

        var childStep = (await store.GetStepsAsync(childRun.Id, Ct)).Single(step => step.StepKey == "perform");
        var childMembership = (await store.GetStepGroupMembershipAsync(childStep.Id, Ct))!;
        childMembership.Role.Should().Be(ActivityGroupMemberRole.Work);
        var childOccurrence = await store.GetGroupOccurrenceAsync(childRun.Id, childMembership.GroupOccurrenceId, Ct);
        childOccurrence.Should().NotBeNull();
        childOccurrence!.GroupKey.Should().Be("child-work");
        childOccurrence.ParentGroupOccurrenceId.Should().NotBeNull();
        (await store.GetGroupOccurrenceAsync(childRun.Id, childOccurrence.ParentGroupOccurrenceId!, Ct))!
            .GroupKey.Should().Be("child-root");

        var requests = authorizer.Requests.ToArray();
        requests.Select(Capability).Should().Contain("child.start").And.Contain("child.wait").And.Contain("child.execute");
        requests.Single(request => Capability(request) == "child.start").Identity.ExecutionId.Should().Be(parentId.ToString("N"));
        requests.Single(request => Capability(request) == "child.wait").Identity.ExecutionId.Should().Be(parentId.ToString("N"));
        var execute = requests.Single(request => Capability(request) == "child.execute");
        execute.Identity.ExecutionId.Should().Be(childRun.Id.ToString("N"));
        execute.Identity.ParentExecutionId.Should().Be(parentId.ToString("N"));
        testOutput.WriteLine("CHILD_AUTHORIZATION_EVIDENCE=" + JsonSerializer.Serialize(new
        {
            DeniedCapability = (string?)null,
            ParentRunId = parentId,
            ChildRunId = childRun.Id,
            RunStatuses = new[]
            {
                new { RunId = parentId, Status = WorkflowStatus.Completed, ParentRunId = (Guid?)null },
                new { RunId = childRun.Id, Status = childRun.Status, ParentRunId = childRun.ParentRunId }
            },
            Requests = RequestEvidence(authorizer),
            ParentSteps = StepEvidence(parentSteps),
            ChildSteps = StepEvidence(await store.GetStepsAsync(childRun.Id, Ct)),
            Memberships = new[]
            {
                MembershipEvidence("parent-start", startMembership),
                MembershipEvidence("parent-wait", waitMembership),
                MembershipEvidence("child-perform", childMembership)
            },
            ChildManifest = childManifest.CanonicalJson
        }));
    }

    [Theory]
    [InlineData("child.start")]
    [InlineData("child.wait")]
    [InlineData("child.execute")]
    public async Task GroupedChildBoundaryDenial_FailsAtTheCorrectAdmissionBoundary(string deniedCapability)
    {
        var authorizer = new RecordingAuthorizer(deniedCapability);
        var store = CreateStore();
        var parent = new GroupedParentWorkflow();
        var child = new GroupedChildWorkflow();
        var registry = Registry(parent, child);
        await using var engine = Engine(store, registry, authorizer);

        var parentId = await engine.StartAsync(ParentName, "1", "input", cancellationToken: Ct);
        await engine.ExecuteAsync(parentId, Ct);
        var completion = () => engine.WaitForCompletionAsync<string>(parentId, cancellationToken: Ct);
        await completion.Should().ThrowAsync<WorkflowExecutionFailedException>();
        (await engine.GetRunAsync(parentId, Ct))!.Status.Should().Be(WorkflowStatus.Failed);
        child.Calls.Should().Be(0);

        var parentSteps = await store.GetStepsAsync(parentId, Ct);
        var startStep = parentSteps.Single(step => step.StepKey == "child:start");
        var startMembership = (await store.GetStepGroupMembershipAsync(startStep.Id, Ct))!;
        startMembership.Role.Should().Be(ActivityGroupMemberRole.Control);

        var children = (await store.GetRunSubtreeAsync(parentId, 5, Ct))
            .Where(run => run.Id != parentId).ToArray();
        if (deniedCapability == "child.start")
        {
            children.Should().BeEmpty("start authorization is checked before creating the child run");
            parentSteps.Should().NotContain(step => step.StepKey == "child:wait");
            authorizer.Requests.Select(Capability).Should().ContainSingle().Which.Should().Be("child.start");
            testOutput.WriteLine("CHILD_AUTHORIZATION_EVIDENCE=" + JsonSerializer.Serialize(new
            {
                DeniedCapability = deniedCapability,
                ParentRunId = parentId,
                RunStatuses = new[]
                {
                    new { RunId = parentId, Status = WorkflowStatus.Failed, ParentRunId = (Guid?)null }
                },
                ChildRuns = children.Select(run => new { run.Id, run.Status, run.ParentRunId }).ToArray(),
                Requests = RequestEvidence(authorizer),
                ParentSteps = StepEvidence(parentSteps),
                Memberships = new[] { MembershipEvidence("parent-start", startMembership) }
            }));
            return;
        }

        children.Should().ContainSingle();
        var childRun = children[0];
        childRun.ParentRunId.Should().Be(parentId);
        var waitStep = parentSteps.Single(step => step.StepKey == "child:wait");
        var waitMembership = (await store.GetStepGroupMembershipAsync(waitStep.Id, Ct))!;
        waitMembership.Role.Should().Be(ActivityGroupMemberRole.Work);
        waitMembership.GroupOccurrenceId.Should().Be(startMembership.GroupOccurrenceId);
        (await store.GetGroupManifestAsync(childRun.Id, Ct))!.CanonicalJson.Should().Be(GroupedChildWorkflow.Manifest.CanonicalJson);

        if (deniedCapability == "child.wait")
        {
            childRun.Status.Should().Be(WorkflowStatus.Pending);
            (await store.GetStepsAsync(childRun.Id, Ct)).Should().BeEmpty("wait authorization prevents child execution");
            authorizer.Requests.Select(Capability).Should().Contain("child.start").And.Contain("child.wait")
                .And.NotContain("child.execute");
            testOutput.WriteLine("CHILD_AUTHORIZATION_EVIDENCE=" + JsonSerializer.Serialize(new
            {
                DeniedCapability = deniedCapability,
                ParentRunId = parentId,
                ChildRunId = childRun.Id,
                RunStatuses = new[]
                {
                    new { RunId = parentId, Status = WorkflowStatus.Failed, ParentRunId = (Guid?)null },
                    new { RunId = childRun.Id, Status = childRun.Status, ParentRunId = childRun.ParentRunId }
                },
                Requests = RequestEvidence(authorizer),
                ParentSteps = StepEvidence(parentSteps),
                ChildSteps = StepEvidence(await store.GetStepsAsync(childRun.Id, Ct)),
                Memberships = new[]
                {
                    MembershipEvidence("parent-start", startMembership),
                    MembershipEvidence("parent-wait", waitMembership)
                },
                ChildManifest = GroupedChildWorkflow.Manifest.CanonicalJson
            }));
            return;
        }

        childRun.Status.Should().Be(WorkflowStatus.Failed);
        var childSteps = await store.GetStepsAsync(childRun.Id, Ct);
        var childWork = childSteps.Single(step => step.StepKey == "perform");
        childWork.Status.Should().Be(StepStatus.Failed);
        var childMembership = (await store.GetStepGroupMembershipAsync(childWork.Id, Ct))!;
        childMembership.Role.Should().Be(ActivityGroupMemberRole.Work);
        authorizer.Requests.Select(Capability).Should().Contain("child.start").And.Contain("child.wait").And.Contain("child.execute");
        var executeRequest = authorizer.Requests.Single(request => Capability(request) == "child.execute");
        executeRequest.Identity.ExecutionId.Should().Be(childRun.Id.ToString("N"));
        executeRequest.Identity.ParentExecutionId.Should().Be(parentId.ToString("N"));
        testOutput.WriteLine("CHILD_AUTHORIZATION_EVIDENCE=" + JsonSerializer.Serialize(new
        {
            DeniedCapability = deniedCapability,
            ParentRunId = parentId,
            ChildRunId = childRun.Id,
            RunStatuses = new[]
            {
                new { RunId = parentId, Status = WorkflowStatus.Failed, ParentRunId = (Guid?)null },
                new { RunId = childRun.Id, Status = childRun.Status, ParentRunId = childRun.ParentRunId }
            },
            Requests = RequestEvidence(authorizer),
            ParentSteps = StepEvidence(parentSteps),
            ChildSteps = StepEvidence(childSteps),
            Memberships = new[]
            {
                MembershipEvidence("parent-start", startMembership),
                MembershipEvidence("parent-wait", waitMembership),
                MembershipEvidence("child-perform", childMembership)
            },
            ChildManifest = GroupedChildWorkflow.Manifest.CanonicalJson
        }));
    }

    [Fact]
    public async Task ParentCancellation_WhileGroupedChildWorkIsBlocked_PreservesMembershipAndEmitsOneCancellationPerRun()
    {
        var authorizer = new RecordingAuthorizer();
        var store = CreateStore();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var parent = new GroupedParentWorkflow();
        var child = new GroupedChildWorkflow(started, release);
        var registry = Registry(parent, child);
        await using var engine = Engine(store, registry, authorizer);

        var parentId = await engine.StartAsync(ParentName, "1", "input", cancellationToken: Ct);
        var execution = engine.ExecuteAsync(parentId, Ct);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        var childRun = (await store.GetRunSubtreeAsync(parentId, 5, Ct)).Single(run => run.Id != parentId);
        var parentStepsBefore = await store.GetStepsAsync(parentId, Ct);
        var childStepsBefore = await store.GetStepsAsync(childRun.Id, Ct);
        var startMembershipBefore = (await store.GetStepGroupMembershipAsync(
            parentStepsBefore.Single(step => step.StepKey == "child:start").Id, Ct))!;
        var waitMembershipBefore = (await store.GetStepGroupMembershipAsync(
            parentStepsBefore.Single(step => step.StepKey == "child:wait").Id, Ct))!;
        var workMembershipBefore = (await store.GetStepGroupMembershipAsync(
            childStepsBefore.Single(step => step.StepKey == "perform").Id, Ct))!;

        await engine.CancelAsync(parentId, actor: "test", reason: "stop grouped child", cancellationToken: Ct);
        release.TrySetResult();
        await execution;
        await engine.CancelAsync(parentId, actor: "test", reason: "repeat cancellation", cancellationToken: Ct);

        (await engine.GetRunAsync(parentId, Ct))!.Status.Should().Be(WorkflowStatus.Cancelled);
        (await engine.GetRunAsync(childRun.Id, Ct))!.Status.Should().Be(WorkflowStatus.Cancelled);
        child.Calls.Should().Be(1, "the blocked operation was entered once and cancellation prevents its result from committing");
        var parentStepsAfter = await store.GetStepsAsync(parentId, Ct);
        var childStepsAfter = await store.GetStepsAsync(childRun.Id, Ct);
        var startMembershipAfter = (await store.GetStepGroupMembershipAsync(
            parentStepsAfter.Single(step => step.StepKey == "child:start").Id, Ct))!;
        var waitMembershipAfter = (await store.GetStepGroupMembershipAsync(
            parentStepsAfter.Single(step => step.StepKey == "child:wait").Id, Ct))!;
        var workMembershipAfter = (await store.GetStepGroupMembershipAsync(
            childStepsAfter.Single(step => step.StepKey == "perform").Id, Ct))!;
        startMembershipAfter.Should().Be(startMembershipBefore);
        waitMembershipAfter.Should().Be(waitMembershipBefore);
        workMembershipAfter.Should().Be(workMembershipBefore);
        childStepsAfter.Single(step => step.StepKey == "perform").Status.Should().Be(StepStatus.Cancelled);
        (await store.GetGroupOccurrenceAsync(parentId, startMembershipBefore.GroupOccurrenceId, Ct))
            .Should().NotBeNull();
        (await store.GetGroupOccurrenceAsync(childRun.Id, workMembershipBefore.GroupOccurrenceId, Ct))
            .Should().NotBeNull();
        (await store.GetGroupManifestAsync(parentId, Ct))!.CanonicalJson.Should().Be(GroupedParentWorkflow.Manifest.CanonicalJson);
        (await store.GetGroupManifestAsync(childRun.Id, Ct))!.CanonicalJson.Should().Be(GroupedChildWorkflow.Manifest.CanonicalJson);

        var cancellationEvents = new Dictionary<Guid, int>();
        foreach (var runId in new[] { parentId, childRun.Id })
        {
            var events = await store.GetEventsAsync(runId, 0, 100, Ct);
            var count = events.Count(item => item.EventType == WorkflowEventTypes.WorkflowCancelled);
            count.Should().Be(1);
            cancellationEvents[runId] = count;
        }
        testOutput.WriteLine("CHILD_CANCELLATION_EVIDENCE=" + JsonSerializer.Serialize(new
        {
            ParentRunId = parentId,
            ChildRunId = childRun.Id,
            RunStatuses = new[]
            {
                new { RunId = parentId, Status = WorkflowStatus.Cancelled, ParentRunId = (Guid?)null },
                new { RunId = childRun.Id, Status = WorkflowStatus.Cancelled, ParentRunId = childRun.ParentRunId }
            },
            ParentSteps = StepEvidence(parentStepsAfter),
            ChildSteps = StepEvidence(childStepsAfter),
            PreservedMemberships = new[]
            {
                new
                {
                    Step = "parent-start",
                    Before = MembershipEvidence("parent-start", startMembershipBefore),
                    After = MembershipEvidence("parent-start", startMembershipAfter)
                },
                new
                {
                    Step = "parent-wait",
                    Before = MembershipEvidence("parent-wait", waitMembershipBefore),
                    After = MembershipEvidence("parent-wait", waitMembershipAfter)
                },
                new
                {
                    Step = "child-perform",
                    Before = MembershipEvidence("child-perform", workMembershipBefore),
                    After = MembershipEvidence("child-perform", workMembershipAfter)
                }
            },
            CancellationEvents = cancellationEvents,
            AuthorizerRequests = RequestEvidence(authorizer)
        }));
    }

    private const string ParentName = "grouped-child-parent";
    private const string ChildName = "grouped-child-worker";

    private WorkflowEngine Engine(SqliteWorkflowStore store, WorkflowRegistry registry, IExecutionAuthorizer authorizer) =>
        new(store, registry, new ZhinuOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(10),
            LeaseDuration = TimeSpan.FromSeconds(3),
            LeaseRenewalInterval = TimeSpan.FromMilliseconds(500),
            ExecutionAuthorization = new WorkflowExecutionAuthorizationOptions("policy", "host-v1", authorizer)
        });

    private static WorkflowRegistry Registry(GroupedParentWorkflow parent, GroupedChildWorkflow child) =>
        new WorkflowRegistry()
            .Register(new WorkflowRegistration<string, string>(
                new WorkflowDefinition { Name = ParentName, Version = "1" }, () => parent, GroupedParentWorkflow.Manifest))
            .Register(new WorkflowRegistration<string, string>(
                new WorkflowDefinition { Name = ChildName, Version = "1" }, () => child, GroupedChildWorkflow.Manifest));

    private static string Capability(ExecutionAuthorizationContext context) =>
        context.Requirements.Single().Capability;

    private static IReadOnlyList<object> RequestEvidence(RecordingAuthorizer authorizer) => authorizer.Requests
        .Select(request => (object)new
        {
            Capability = Capability(request),
            request.Identity.ExecutionId,
            request.Identity.ParentExecutionId
        }).ToArray();

    private static IReadOnlyList<object> StepEvidence(IEnumerable<WorkflowStepRun> steps) => steps
        .OrderBy(step => step.StepKey, StringComparer.Ordinal)
        .Select(step => (object)new { step.StepKey, step.Status, step.Revision, step.Attempt })
        .ToArray();

    private static object MembershipEvidence(string boundary, ActivityGroupStepMembership membership) => new
    {
        Boundary = boundary,
        membership.WorkflowRunId,
        membership.StepKey,
        membership.StepRevision,
        membership.Role,
        membership.GroupOccurrenceId
    };

    private static WorkflowAuthorizationDeclaration Declaration(string capability) => new([
        new ExecutionRequirement("workflow.capability", 1, capability, "resource", null)]);

    private sealed class RecordingAuthorizer(string? deniedCapability = null) : IExecutionAuthorizer
    {
        private readonly ConcurrentQueue<ExecutionAuthorizationContext> requests = new();
        public IReadOnlyCollection<ExecutionAuthorizationContext> Requests => requests.ToArray();

        public ValueTask<ExecutionAuthorizationResult> AuthorizeAsync(
            ExecutionAuthorizationContext context, CancellationToken cancellationToken = default)
        {
            requests.Enqueue(context);
            var denied = context.Requirements.Single().Capability == deniedCapability;
            var now = DateTimeOffset.UtcNow;
            return ValueTask.FromResult(new ExecutionAuthorizationResult(
                denied ? ExecutionAuthorizationDecision.Denied : ExecutionAuthorizationDecision.Allowed,
                context.AuthorizationRequestId,
                "policy",
                Guid.NewGuid().ToString("N"),
                now,
                denied ? null : now.AddMinutes(1)));
        }
    }

    private sealed class GroupedParentWorkflow : IWorkflow<string, string>
    {
        public static ActivityGroupManifest Manifest { get; } = ActivityGroupManifest.Create([
            new ActivityGroupDefinition("invocation", ActivityGroupKind.Macro, "Child invocation",
                provenance: new ActivityGroupMacroProvenance("Child", "child-v1", "invoke-child"))]);

        public Task<string> RunAsync(WorkflowContext context, string input, CancellationToken cancellationToken)
        {
            var occurrence = context.GetActivityGroup("invocation");
            return context.StartChildAsync<string, string>("child", ChildName, "1", input,
                new ChildRunOptions
                {
                    StartGroup = new ActivityGroupStepBinding(
                        new ActivityGroupMembership("invocation", ActivityGroupMemberRole.Control), occurrence),
                    WaitGroup = new ActivityGroupStepBinding(
                        new ActivityGroupMembership("invocation", ActivityGroupMemberRole.Work), occurrence),
                    StartAuthorization = Declaration("child.start"),
                    WaitAuthorization = Declaration("child.wait")
                }, cancellationToken);
        }
    }

    private sealed class GroupedChildWorkflow(
        TaskCompletionSource? started = null,
        TaskCompletionSource? release = null) : IWorkflow<string, string>
    {
        private const string ExecuteCapability = "child.execute";
        private int calls;
        public static ActivityGroupManifest Manifest { get; } = ActivityGroupManifest.Create([
            new ActivityGroupDefinition("child-root", ActivityGroupKind.Logical, "Child root"),
            new ActivityGroupDefinition("child-work", ActivityGroupKind.Macro, "Child work", "child-root",
                provenance: new ActivityGroupMacroProvenance("Child work", "child-work-v1", "child-work-call"))]);

        public int Calls => Volatile.Read(ref calls);

        public Task<string> RunAsync(WorkflowContext context, string input, CancellationToken cancellationToken)
        {
            var parent = context.GetActivityGroup("child-root");
            var group = context.GetActivityGroup("child-work", parent);
            return context.StepAsync("perform", input, async (value, _, ct) =>
            {
                Interlocked.Increment(ref calls);
                started?.TrySetResult();
                if (release is not null) await release.Task.WaitAsync(ct).ConfigureAwait(false);
                return "child:" + value;
            }, new StepOptions
            {
                Authorization = Declaration(ExecuteCapability),
                Group = new ActivityGroupStepBinding(new ActivityGroupMembership("child-work"), group)
            }, cancellationToken);
        }
    }
}
