using System.Collections.Concurrent;
using System.Reflection;
using FluentAssertions;

namespace Penghou.Zhinu.Sqlite.Tests;

public sealed class ActivityGroupFanOutBodyTests : WorkflowEngineTestBase
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Body_PersistsTwoOperationsPerItemAndSnapshotProvesNestedMembershipAndClosure()
    {
        var store = CreateStore();
        var workflow = new BodyWorkflow();
        await using var engine = Engine(store, workflow);
        var id = await engine.StartAsync("fanout-body", "1", "input", cancellationToken: Ct);
        await engine.ExecuteAsync(id, Ct);
        (await engine.WaitForCompletionAsync<string>(id, cancellationToken: Ct)).Should().Be("a-loaded:done|b-loaded:done");
        workflow.Calls.Should().HaveCount(4);

        var facts = (await store.GetStepsAsync(id, Ct)).Where(step => step.StepKey.Contains(":body:", StringComparison.Ordinal)).ToArray();
        facts.Should().HaveCount(4);
        var itemOccurrences = new HashSet<string>(StringComparer.Ordinal);
        var nestedOccurrences = new HashSet<string>(StringComparer.Ordinal);
        foreach (var step in facts)
        {
            var membership = await store.GetStepGroupMembershipAsync(step.Id, Ct);
            membership.Should().NotBeNull();
            membership!.StepRevision.Should().Be(step.Revision);
            membership.Role.Should().Be(ActivityGroupMemberRole.Work);
            var occurrence = await store.GetGroupOccurrenceAsync(id, membership.GroupOccurrenceId, Ct);
            occurrence.Should().NotBeNull();
            occurrence!.ExecutionCoordinate.Should().Contain("\"version\":2");
            if (occurrence.GroupKey == "item") itemOccurrences.Add(occurrence.GroupOccurrenceId);
            if (occurrence.GroupKey == "detail") nestedOccurrences.Add(occurrence.GroupOccurrenceId);
        }
        itemOccurrences.Should().HaveCount(2);
        nestedOccurrences.Should().HaveCount(2);

        using var reader = new WorkflowGroupReader(store);
        var snapshot = (await reader.GetRunGroupSnapshotAsync(id, Ct)).Value!;
        var page = await reader.QueryGroupExpansionsAsync(id, snapshot.SnapshotToken, cancellationToken: Ct);
        page.Status.Should().Be(WorkflowGroupReadStatus.Success);
        var expansion = page.Value!.Items.Single();
        expansion.BodyContract.Should().Be("body-contract-1");
        expansion.IsComplete.Should().BeTrue();
        expansion.Closures.Should().ContainSingle().Which.Steps.Should().HaveCount(6);
    }

    [Fact]
    public async Task PartialBodyFailure_ReusesCompletedOperationAndDependentsRestartLeavesSiblingUntouched()
    {
        var store = CreateStore();
        var workflow = new BodyWorkflow { FailSecondForAOnce = true };
        await using var engine = Engine(store, workflow);
        var id = await engine.StartAsync("fanout-body", "1", "input", cancellationToken: Ct);
        await engine.ExecuteAsync(id, Ct);
        (await store.GetRunAsync(id, Ct))!.Status.Should().Be(WorkflowStatus.Failed);
        (await store.GetGroupFanOutAsync(id, "items", Ct))!.IsComplete.Should().BeFalse();
        workflow.Calls["a:load"].Should().Be(1);
        workflow.Calls["a:finish"].Should().Be(1);
        workflow.Calls.Should().NotContainKey("b:load");

        var failedKey = workflow.OperationStepKey("a", "finish");
        await engine.RestartStepAsync(id, failedKey, new RestartStepOptions { Mode = StepRestartMode.Dependents }, Ct);
        workflow.FailSecondForAOnce = false;
        await engine.ExecuteAsync(id, Ct);
        (await engine.WaitForCompletionAsync<string>(id, cancellationToken: Ct)).Should().Be("a-loaded:done|b-loaded:done");
        workflow.Calls["a:load"].Should().Be(1);
        workflow.Calls["a:finish"].Should().Be(2);
        workflow.Calls["b:load"].Should().Be(1);
        workflow.Calls["b:finish"].Should().Be(1);

        await engine.RestartStepAsync(id, workflow.OperationStepKey("a", "load"),
            new RestartStepOptions { Mode = StepRestartMode.Dependents }, Ct);
        (await store.GetGroupFanOutAsync(id, "items", Ct))!.IsComplete.Should().BeFalse();
        await engine.ExecuteAsync(id, Ct);
        workflow.Calls["a:load"].Should().Be(2);
        workflow.Calls["a:finish"].Should().Be(3);
        workflow.Calls["b:load"].Should().Be(1);
        workflow.Calls["b:finish"].Should().Be(1);
        (await store.GetGroupFanOutAsync(id, "items", Ct))!.IsComplete.Should().BeTrue();
    }

    [Fact]
    public async Task StepOnlyRestartOfBodyOperation_IsRejectedBeforeAnyRevisionChanges()
    {
        var store = CreateStore();
        var workflow = new BodyWorkflow();
        await using var engine = Engine(store, workflow);
        var id = await engine.StartAsync("fanout-body", "1", "input", cancellationToken: Ct);
        await engine.ExecuteAsync(id, Ct);
        var before = await store.GetStepsAsync(id, Ct);
        var restart = () => engine.RestartStepAsync(id, workflow.OperationStepKey("a", "load"),
            new RestartStepOptions { Mode = StepRestartMode.StepOnly }, Ct);
        await restart.Should().ThrowAsync<WorkflowStateException>();
        (await store.GetStepsAsync(id, Ct)).Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task BodyContractChange_IsRejectedBeforeBodyClaimsOnReplay()
    {
        var store = CreateStore();
        var workflow = new BodyWorkflow();
        await using var engine = Engine(store, workflow);
        var id = await engine.StartAsync("fanout-body", "1", "input", cancellationToken: Ct);
        await engine.ExecuteAsync(id, Ct);
        var calls = workflow.Calls.Values.Sum();
        await engine.RestartStepAsync(id, "tail", new RestartStepOptions { Mode = StepRestartMode.StepOnly }, Ct);
        workflow.BodyContract = "body-contract-2";
        await engine.ExecuteAsync(id, Ct);
        (await store.GetRunAsync(id, Ct))!.Status.Should().Be(WorkflowStatus.Failed);
        workflow.Calls.Values.Sum().Should().Be(calls);
    }

    [Fact]
    public async Task CompletedBodyItemAndNestedGroupAreClosedWhileSiblingIsRunning()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = CreateStore();
        var workflow = new BodyWorkflow { BlockedEntered = entered, BlockedRelease = release, BlockedItem = "b" };
        await using var engine = Engine(store, workflow);
        var id = await engine.StartAsync("fanout-body", "1", "input", cancellationToken: Ct);
        var execution = engine.ExecuteAsync(id, Ct);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(20), Ct);
            using var reader = new WorkflowGroupReader(CreateStore());
            var snapshot = (await reader.GetRunGroupSnapshotAsync(id, Ct)).Value!;
            var expansion = (await reader.QueryGroupExpansionsAsync(id, snapshot.SnapshotToken, cancellationToken: Ct)).Value!.Items.Single();
            expansion.IsComplete.Should().BeFalse();
            var completedItem = expansion.Items.Single(item => item.ItemKey == "a");
            var itemState = (await reader.GetGroupStateAsync(id, snapshot.SnapshotToken, completedItem.OccurrenceId, Ct)).Value!.State;
            itemState.Status.Should().Be(StepStatus.Completed);
            itemState.IsExpansionClosed.Should().BeTrue();
            itemState.TotalCount.Should().BeGreaterThan(0);

            var finish = (await store.GetStepsAsync(id, Ct)).Single(step => step.StepKey == workflow.OperationStepKey("a", "finish"));
            var nestedOccurrence = (await store.GetStepGroupMembershipAsync(finish.Id, Ct))!.GroupOccurrenceId;
            var nestedState = (await reader.GetGroupStateAsync(id, snapshot.SnapshotToken, nestedOccurrence, Ct)).Value!.State;
            nestedState.Status.Should().Be(StepStatus.Completed);
            nestedState.IsExpansionClosed.Should().BeTrue();
            nestedState.TotalCount.Should().Be(1);

            var activeItem = expansion.Items.Single(item => item.ItemKey == "b");
            var activeState = (await reader.GetGroupStateAsync(id, snapshot.SnapshotToken, activeItem.OccurrenceId, Ct)).Value!.State;
            activeState.Status.Should().Be(StepStatus.Running);
            activeState.IsExpansionClosed.Should().BeFalse();
        }
        finally
        {
            release.TrySetResult();
            await execution;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletedBodyItemRemainsClosedWhenSiblingFailsOrRunIsCancelled(bool cancel)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = CreateStore();
        var workflow = new BodyWorkflow
        {
            BlockedEntered = entered,
            BlockedRelease = release,
            BlockedItem = "b",
            FailBlockedItem = !cancel
        };
        await using var engine = Engine(store, workflow);
        var id = await engine.StartAsync("fanout-body", "1", "input", cancellationToken: Ct);
        var execution = engine.ExecuteAsync(id, Ct);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(20), Ct);
            if (cancel)
            {
                await engine.CancelAsync(id, Ct);
            }
            else
            {
                release.TrySetResult();
            }
            await execution;

            using var reader = new WorkflowGroupReader(CreateStore());
            var snapshot = (await reader.GetRunGroupSnapshotAsync(id, Ct)).Value!;
            var expansion = (await reader.QueryGroupExpansionsAsync(id, snapshot.SnapshotToken, cancellationToken: Ct)).Value!.Items.Single();
            var completedItem = expansion.Items.Single(item => item.ItemKey == "a");
            var state = (await reader.GetGroupStateAsync(id, snapshot.SnapshotToken, completedItem.OccurrenceId, Ct)).Value!.State;
            state.Status.Should().Be(StepStatus.Completed);
            state.IsExpansionClosed.Should().BeTrue();
            state.RunStatus.Should().Be(cancel ? WorkflowStatus.Cancelled : WorkflowStatus.Failed);
        }
        finally
        {
            release.TrySetResult();
            await execution;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BodyItemClosureStaysConservativeWhenOptionalInputHashesAreMissingOrStale(bool omitHashes)
    {
        var backing = CreateStore();
        var workflow = new BodyWorkflow();
        await using (var engine = Engine(backing, workflow))
        {
            var id = await engine.StartAsync("fanout-body", "1", "input", cancellationToken: Ct);
            await engine.ExecuteAsync(id, Ct);
            await engine.WaitForCompletionAsync<string>(id, cancellationToken: Ct);
        }
        var projected = DispatchProxy.Create<IProjectionStore, SnapshotProjectionProxy>();
        ((SnapshotProjectionProxy)(object)projected).Backing = backing;
        ((SnapshotProjectionProxy)(object)projected).Projection = data => data with
        {
            Run = data.Run with { Status = WorkflowStatus.Running },
            Expansions = data.Expansions.Select(expansion => expansion with
            {
                IsComplete = false,
                Items = expansion.Items.Select(item => item with
                {
                    InputHash = omitHashes ? null : new string('0', 64)
                }).ToArray()
            }).ToArray(),
            Steps = data.Steps.Select(step => step with { InputHash = omitHashes ? null : step.InputHash }).ToArray()
        };

        using var reader = new WorkflowGroupReader(projected);
        var snapshot = (await reader.GetRunGroupSnapshotAsync(workflow.RunId, Ct)).Value!;
        var expansionFact = (await reader.QueryGroupExpansionsAsync(workflow.RunId, snapshot.SnapshotToken, cancellationToken: Ct)).Value!.Items.Single();
        var item = expansionFact.Items.Single(item => item.ItemKey == "a");
        var state = (await reader.GetGroupStateAsync(workflow.RunId, snapshot.SnapshotToken, item.OccurrenceId, Ct)).Value!.State;
        state.IsExpansionClosed.Should().BeFalse();
        state.TotalCount.Should().BeNull();
    }

    [Fact]
    public async Task SwallowedBodyFailure_CannotCreateAClosureOrCompleteTheRun()
    {
        var store = CreateStore();
        var workflow = new BodyWorkflow { FailSecondForAOnce = true, SwallowBodyFailure = true };
        await using var engine = Engine(store, workflow);
        var id = await engine.StartAsync("fanout-body", "1", "input", cancellationToken: Ct);
        await engine.ExecuteAsync(id, Ct);
        (await store.GetRunAsync(id, Ct))!.Status.Should().Be(WorkflowStatus.Failed);
        (await store.GetGroupFanOutAsync(id, "items", Ct))!.IsComplete.Should().BeFalse();
    }

    [Fact]
    public async Task DuplicateBodyOperationRegistration_FailsBeforeItemCompletion()
    {
        var store = CreateStore();
        var workflow = new BodyWorkflow { DuplicateOperationRegistration = true };
        await using var engine = Engine(store, workflow);
        var id = await engine.StartAsync("fanout-body", "1", "input", cancellationToken: Ct);
        await engine.ExecuteAsync(id, Ct);

        (await store.GetRunAsync(id, Ct))!.Status.Should().Be(WorkflowStatus.Failed);
        (await store.GetGroupFanOutAsync(id, "items", Ct))!.IsComplete.Should().BeFalse();
        workflow.Calls["a:load"].Should().Be(1);
        (await store.GetStepsAsync(id, Ct)).Should().NotContain(step => step.StepKey == "items.a");
    }

    [Fact]
    public async Task RawAndTamperedBodyClaims_AreRejectedBeforeOperationCallback()
    {
        var backing = CreateStore();
        var raw = new BodyWorkflow { UseRawGroupedStep = true };
        await using (var engine = Engine(backing, raw))
        {
            var id = await engine.StartAsync("fanout-body", "1", "input", cancellationToken: Ct);
            await engine.ExecuteAsync(id, Ct);
            (await backing.GetRunAsync(id, Ct))!.Status.Should().Be(WorkflowStatus.Failed);
            raw.Calls.Should().BeEmpty();
        }

        var store = DispatchProxy.Create<IBodyStore, TamperBodyClaimProxy>();
        ((TamperBodyClaimProxy)(object)store).Backing = backing;
        ((TamperBodyClaimProxy)(object)store).Tamper = true;
        var tampered = new BodyWorkflow();
        await using var tamperedEngine = Engine(store, tampered);
        var tamperedId = await tamperedEngine.StartAsync("fanout-body", "1", "input", cancellationToken: Ct);
        await tamperedEngine.ExecuteAsync(tamperedId, Ct);
        (await backing.GetRunAsync(tamperedId, Ct))!.Status.Should().Be(WorkflowStatus.Failed);
        tampered.Calls.Should().BeEmpty();
        (await backing.GetGroupFanOutAsync(tamperedId, "items", Ct))!.IsComplete.Should().BeFalse();
    }

    private WorkflowEngine Engine(IWorkflowStore store, BodyWorkflow workflow) => new(store,
        new WorkflowRegistry().Register(new WorkflowRegistration<string, string>(
            new WorkflowDefinition { Name = "fanout-body", Version = "1" }, () => workflow, Manifest())),
        new ZhinuOptions { PollInterval = TimeSpan.FromMilliseconds(10) });

    private static ActivityGroupManifest Manifest() => ActivityGroupManifest.Create([
        new ActivityGroupDefinition("root", ActivityGroupKind.Logical, "Root"),
        new ActivityGroupDefinition("item", ActivityGroupKind.Macro, "Item", "root", provenance:
            new ActivityGroupMacroProvenance("ItemMacro", "fingerprint", "item-invocation")),
        new ActivityGroupDefinition("detail", ActivityGroupKind.Macro, "Detail", "item", provenance:
            new ActivityGroupMacroProvenance("DetailMacro", "fingerprint", "detail-invocation"))]);

    private sealed class BodyWorkflow : IWorkflow<string, string>
    {
        private int failedOnce;
        public ConcurrentDictionary<string, int> Calls { get; } = new(StringComparer.Ordinal);
        public bool FailSecondForAOnce { get; set; }
        public bool SwallowBodyFailure { get; set; }
        public bool UseRawGroupedStep { get; set; }
        public bool DuplicateOperationRegistration { get; set; }
        public string BodyContract { get; set; } = "body-contract-1";
        public TaskCompletionSource? BlockedEntered { get; set; }
        public TaskCompletionSource? BlockedRelease { get; set; }
        public string BlockedItem { get; set; } = "b";
        public bool FailBlockedItem { get; set; }
        public Guid RunId { get; private set; }

        public string OperationStepKey(string item, string operation) =>
            "items:body:" + Encode(item) + ":" + Encode(operation);

        public async Task<string> RunAsync(WorkflowContext context, string input, CancellationToken ct)
        {
            RunId = context.WorkflowRunId;
            var selected = await context.StepAsync("select", input,
                (_, _) => Task.FromResult(new[] { "a", "b" }), cancellationToken: ct);
            var fanOptions = new StepOptions
            {
                DependsOn = ["select"],
                FanOutGroup = new ActivityGroupFanOutBinding(
                    new ActivityGroupMembership("item", ActivityGroupMemberRole.Control), "select", context.GetActivityGroup("root"))
            };
            try
            {
                var results = await context.FanOutBodyAsync("items", selected, item => item,
                    async (item, scope, token) =>
                    {
                        if (UseRawGroupedStep)
                            return await context.StepAsync("raw", item, (value, _) =>
                            {
                                Calls.AddOrUpdate(item + ":raw", 1, (_, count) => count + 1);
                                return Task.FromResult(value);
                            }, new StepOptions { Group = scope.Group }, token);
                        var loaded = await scope.StepAsync<string, string>("load", item,
                            async (value, _, operationToken) =>
                            {
                                Calls.AddOrUpdate(item + ":load", 1, (_, count) => count + 1);
                                if (item == BlockedItem && BlockedEntered is not null && BlockedRelease is not null)
                                {
                                    BlockedEntered.TrySetResult();
                                    await BlockedRelease.Task.WaitAsync(operationToken);
                                }
                                if (item == BlockedItem && FailBlockedItem)
                                    throw new InvalidOperationException("injected sibling failure");
                                return value + "-loaded";
                            }, cancellationToken: token);
                        if (DuplicateOperationRegistration)
                            _ = scope.StepAsync<string, string>("load", item,
                                (value, _, _) => Task.FromResult(value), cancellationToken: token);
                        var detail = scope.GetActivityGroup("detail");
                        var done = await scope.StepAsync<string, string>("finish", loaded + ":done",
                            (value, _, _) =>
                            {
                                Calls.AddOrUpdate(item + ":finish", 1, (_, count) => count + 1);
                                if (item == "a" && FailSecondForAOnce && Interlocked.CompareExchange(ref failedOnce, 1, 0) == 0)
                                    throw new InvalidOperationException("injected body failure");
                                return Task.FromResult(value);
                            }, new StepOptions
                            {
                                Group = new ActivityGroupStepBinding(new ActivityGroupMembership("detail"), detail),
                                DependsOn = [scope.GetStepKey("load")],
                                Retry = new RetryPolicy { MaxAttempts = 1 }
                            }, token);
                        return done;
                    }, BodyContract, new FanOutOptions { MaxDegreeOfParallelism = 1 }, fanOptions, ct);
                return await context.StepAsync("tail", string.Join('|', results), (value, _) => Task.FromResult(value), cancellationToken: ct);
            }
            catch when (SwallowBodyFailure) { return "swallowed"; }
        }

        private static string Encode(string value) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public interface IBodyStore : IWorkflowStore, IWorkflowGroupRepository, IWorkflowGroupFanOutBodyRepository { }

    public interface IProjectionStore : IWorkflowStore, IWorkflowGroupSnapshotRepository { }

    public class SnapshotProjectionProxy : DispatchProxy
    {
        public IWorkflowStore? Backing { get; set; }
        public Func<WorkflowGroupSnapshotData, WorkflowGroupSnapshotData>? Projection { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (Backing is null || targetMethod is null || args is null) throw new InvalidOperationException();
            if (targetMethod.Name == nameof(IWorkflowGroupSnapshotRepository.ReadGroupSnapshotAsync))
                return ProjectAsync((Guid)args[0]!, (WorkflowGroupReadLimits)args[1]!, (CancellationToken)args[2]!);
            return targetMethod.Invoke(Backing, args);
        }

        private async ValueTask<WorkflowGroupQueryResult<WorkflowGroupSnapshotData>> ProjectAsync(Guid runId,
            WorkflowGroupReadLimits limits, CancellationToken cancellationToken)
        {
            var repository = (IWorkflowGroupSnapshotRepository)Backing!;
            var result = await repository.ReadGroupSnapshotAsync(runId, limits, cancellationToken);
            if (result.Value is null || Projection is null) return result;
            return result with { Value = Projection(result.Value) };
        }
    }

    public class TamperBodyClaimProxy : DispatchProxy
    {
        public IWorkflowStore? Backing { get; set; }
        public bool Tamper { get; set; }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (Backing is null || targetMethod is null || args is null) throw new InvalidOperationException();
            if (Tamper && targetMethod.Name == nameof(IWorkflowStore.ClaimStepAsync) && args[0] is StepClaimRequest request &&
                request.FanOut?.BodyOperationKey is not null)
                args[0] = request with { FanOut = request.FanOut with { ExpansionId = "wrong-expansion" } };
            return targetMethod.Invoke(Backing, args);
        }
    }
}
