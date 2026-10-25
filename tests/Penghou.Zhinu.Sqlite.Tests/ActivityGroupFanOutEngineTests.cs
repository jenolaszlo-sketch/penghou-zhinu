using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.ExceptionServices;
using FluentAssertions;
using Microsoft.Data.Sqlite;

namespace Penghou.Zhinu.Sqlite.Tests;

public sealed class ActivityGroupFanOutEngineTests : WorkflowEngineTestBase
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ConcurrentSiblings_AreClaimedWithIsolatedMembershipBeforeEitherCompletes()
    {
        var store = CreateStore();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workflow = new ConcurrentWorkflow(entered, release);
        await using var engine = new WorkflowEngine(store, new WorkflowRegistry().Register(
            new WorkflowRegistration<string, string>(new WorkflowDefinition { Name = "concurrent", Version = "1" },
                () => workflow, Manifest())), new ZhinuOptions());
        var id = await engine.StartAsync("concurrent", "1", "input", cancellationToken: Ct);
        var execution = engine.ExecuteAsync(id, Ct);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(20), Ct);
            var siblings = (await store.GetStepsAsync(id, Ct)).Where(step => step.StepKey.StartsWith("items.", StringComparison.Ordinal)).ToArray();
            siblings.Should().HaveCount(2).And.OnlyContain(step => step.Status == StepStatus.Running);
            var a = await store.GetStepGroupMembershipAsync(siblings.Single(step => step.StepKey == "items.a").Id, Ct);
            var b = await store.GetStepGroupMembershipAsync(siblings.Single(step => step.StepKey == "items.b").Id, Ct);
            a!.GroupOccurrenceId.Should().NotBe(b!.GroupOccurrenceId);
            (await store.GetGroupFanOutAsync(id, "items", Ct))!.IsComplete.Should().BeFalse();
        }
        finally { release.TrySetResult(); }
        await execution;
        (await engine.WaitForCompletionAsync<string>(id, cancellationToken: Ct)).Should().Be("a:b");
        (await store.GetGroupFanOutAsync(id, "items", Ct))!.IsComplete.Should().BeTrue();
    }

    [Theory]
    [InlineData(Variant.FunctionalKeyed)]
    [InlineData(Variant.FunctionalIndex)]
    [InlineData(Variant.ClassKeyed)]
    [InlineData(Variant.ClassIndex)]
    public async Task NativeOverloads_PreserveResultsAndDistinctItemParentMembership(Variant variant)
    {
        var store = CreateStore();
        var workflow = new FanWorkflow(variant);
        await using var engine = Engine(store, workflow);
        var id = await engine.StartAsync("fan-groups", "1", "input", cancellationToken: Ct);
        await engine.ExecuteAsync(id, Ct);
        (await engine.WaitForCompletionAsync<string>(id, cancellationToken: Ct)).Should().Be("A:B");
        workflow.Calls.Values.Sum().Should().Be(2);
        var state = await store.GetGroupFanOutAsync(id, "items", Ct);
        state.Should().NotBeNull();
        state!.IsComplete.Should().BeTrue();
        state.ItemCount.Should().Be(2);
        (await store.GetRunAsync(id, Ct))!.GroupFanOutUsed.Should().BeTrue();
        var parentId = ActivityGroupOccurrence.Create(id, Manifest(), "root", null, "[]").GroupOccurrenceId;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var step in (await store.GetStepsAsync(id, Ct)).Where(step => step.StepKey.StartsWith("items.", StringComparison.Ordinal)))
        {
            var member = await store.GetStepGroupMembershipAsync(step.Id, Ct);
            member.Should().NotBeNull();
            member!.StepRevision.Should().Be(step.Revision);
            member.Role.Should().Be(ActivityGroupMemberRole.Work);
            var occurrence = await store.GetGroupOccurrenceAsync(id, member.GroupOccurrenceId, Ct);
            occurrence!.GroupKey.Should().Be("work");
            occurrence.ParentGroupOccurrenceId.Should().Be(parentId);
            occurrence.ExecutionCoordinate.Should().Contain("\"version\":2");
            ids.Add(occurrence.GroupOccurrenceId);
        }
        ids.Should().HaveCount(2);
    }

    [Fact]
    public async Task KeyedReorder_ReusesItemsAndPreservesCallerResultOrder()
    {
        var store = CreateStore();
        var workflow = new FanWorkflow(Variant.FunctionalKeyed);
        await using var engine = Engine(store, workflow);
        var id = await engine.StartAsync("fan-groups", "1", "input", cancellationToken: Ct);
        await engine.ExecuteAsync(id, Ct);
        var before = await store.GetGroupFanOutAsync(id, "items", Ct);
        workflow.Change = "reverse";
        await engine.RestartStepAsync(id, "finish", new RestartStepOptions { Mode = StepRestartMode.StepOnly }, Ct);
        await engine.ExecuteAsync(id, Ct);
        (await engine.WaitForCompletionAsync<string>(id, cancellationToken: Ct)).Should().Be("B:A");
        workflow.Calls.Values.Sum().Should().Be(2);
        (await store.GetGroupFanOutAsync(id, "items", Ct)).Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task ItemRestart_AppendsClosureForNewRevisionAndReusesSibling()
    {
        var store = CreateStore();
        var workflow = new FanWorkflow(Variant.FunctionalKeyed);
        await using var engine = Engine(store, workflow);
        var id = await engine.StartAsync("fan-groups", "1", "input", cancellationToken: Ct);
        await engine.ExecuteAsync(id, Ct);
        var old = (await store.GetStepsAsync(id, Ct)).Single(step => step.StepKey == "items.a");
        var membership = await store.GetStepGroupMembershipAsync(old.Id, Ct);
        await engine.RestartStepAsync(id, "items.a", new RestartStepOptions { Mode = StepRestartMode.StepOnly }, Ct);
        (await store.GetGroupFanOutAsync(id, "items", Ct))!.IsComplete.Should().BeFalse();
        await engine.ExecuteAsync(id, Ct);
        (await engine.WaitForCompletionAsync<string>(id, cancellationToken: Ct)).Should().Be("A:B");
        workflow.Calls["a"].Should().Be(2);
        workflow.Calls["b"].Should().Be(1);
        var current = (await store.GetStepsAsync(id, Ct)).Single(step => step.StepKey == "items.a");
        current.Revision.Should().Be(old.Revision + 1);
        (await store.GetStepGroupMembershipAsync(current.Id, Ct))!.GroupOccurrenceId.Should().Be(membership!.GroupOccurrenceId);
        (await store.GetGroupFanOutAsync(id, "items", Ct))!.IsComplete.Should().BeTrue();
        (await CountAsync("workflow_group_fanout_closures")).Should().Be(2);
    }

    [Fact]
    public async Task SelectionRevisionRestart_KeepsOccurrencesButBindsNewExpansion()
    {
        var store = CreateStore();
        var workflow = new FanWorkflow(Variant.FunctionalKeyed);
        await using var engine = Engine(store, workflow);
        var id = await engine.StartAsync("fan-groups", "1", "input", cancellationToken: Ct);
        await engine.ExecuteAsync(id, Ct);
        var before = await store.GetGroupFanOutAsync(id, "items", Ct);
        var memberIds = (await store.GetStepsAsync(id, Ct)).Where(step => step.StepKey.StartsWith("items.", StringComparison.Ordinal)).Select(step => step.Id).ToArray();
        await engine.RestartStepAsync(id, "select", new RestartStepOptions { Mode = StepRestartMode.StepOnly }, Ct);
        (await store.GetGroupFanOutAsync(id, "items", Ct))!.IsComplete.Should().BeFalse();
        await engine.ExecuteAsync(id, Ct);
        (await engine.WaitForCompletionAsync<string>(id, cancellationToken: Ct)).Should().Be("A:B");
        var after = await store.GetGroupFanOutAsync(id, "items", Ct);
        after!.ExpansionId.Should().NotBe(before!.ExpansionId);
        after.BoundaryRevision.Should().Be(before.BoundaryRevision + 1);
        after.PlanDigest.Should().Be(before.PlanDigest);
        after.IsComplete.Should().BeTrue();
        (await store.GetStepsAsync(id, Ct)).Where(step => step.StepKey.StartsWith("items.", StringComparison.Ordinal)).Select(step => step.Id).Should().BeEquivalentTo(memberIds);
        workflow.Calls.Values.Sum().Should().Be(2);
    }

    [Theory]
    [InlineData("changed")]
    [InlineData("add")]
    [InlineData("remove")]
    [InlineData("positional-reverse")]
    public async Task ChangedSelection_RejectsBeforeAnyAdditionalItemCallback(string change)
    {
        var store = CreateStore();
        var workflow = new FanWorkflow(change == "positional-reverse" ? Variant.FunctionalIndex : Variant.FunctionalKeyed);
        await using var engine = Engine(store, workflow);
        var id = await engine.StartAsync("fan-groups", "1", "input", cancellationToken: Ct);
        await engine.ExecuteAsync(id, Ct);
        var before = await store.GetGroupFanOutAsync(id, "items", Ct);
        workflow.Change = change;
        await engine.RestartStepAsync(id, "finish", new RestartStepOptions { Mode = StepRestartMode.StepOnly }, Ct);
        await engine.ExecuteAsync(id, Ct);
        var result = () => engine.WaitForCompletionAsync<string>(id, cancellationToken: Ct);
        await result.Should().ThrowAsync<WorkflowExecutionFailedException>();
        workflow.Calls.Values.Sum().Should().Be(2);
        (await store.GetGroupFanOutAsync(id, "items", Ct)).Should().BeEquivalentTo(before);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedChunk_RemainsIncompleteEvenWhenWorkflowSwallowsFailure(bool swallow)
    {
        var store = CreateStore();
        var workflow = new FanWorkflow(Variant.FunctionalKeyed) { Fail = true, Swallow = swallow };
        await using var engine = Engine(store, workflow);
        var id = await engine.StartAsync("fan-groups", "1", "input", cancellationToken: Ct);
        await engine.ExecuteAsync(id, Ct);
        (await store.GetRunAsync(id, Ct))!.Status.Should().Be(WorkflowStatus.Failed);
        workflow.Calls.Keys.Should().Equal("a");
        (await store.GetGroupFanOutAsync(id, "items", Ct))!.IsComplete.Should().BeFalse();
        (await CountAsync("workflow_group_fanout_items")).Should().Be(2);
        (await CountAsync("workflow_group_fanout_closures")).Should().Be(0);
    }

    [Fact]
    public async Task Retry_RetainsOneMembershipAndOccurrence()
    {
        var store = CreateStore();
        var workflow = new FanWorkflow(Variant.FunctionalKeyed) { RetryOnce = true };
        await using var engine = Engine(store, workflow);
        var output = await engine.RunAsync<string, string>("fan-groups", "1", "input", cancellationToken: Ct);
        output.Should().Be("A:B");
        workflow.Calls["a"].Should().Be(2);
        (await CountAsync("workflow_group_occurrences")).Should().Be(3);
        (await CountAsync("workflow_step_group_memberships")).Should().Be(2);
    }

    [Fact]
    public async Task ClassCompensation_ReplaysEnumerationWithoutNewAdmissionOrClosure()
    {
        var store = CreateStore();
        var workflow = new FanWorkflow(Variant.ClassKeyed) { Compensation = StepCompensationMode.Enabled };
        await using var engine = Engine(store, workflow);
        var id = await engine.StartAsync("fan-groups", "1", "input", cancellationToken: Ct);
        await engine.ExecuteAsync(id, Ct);
        (await engine.WaitForCompletionAsync<string>(id, cancellationToken: Ct)).Should().Be("A:B");
        var before = await store.GetGroupFanOutAsync(id, "items", Ct);
        await engine.RollbackAsync(id, cancellationToken: Ct);
        workflow.Compensated.Should().BeEquivalentTo(["a:A", "b:B"]);
        workflow.Calls.Values.Sum().Should().Be(2);
        (await store.GetRunAsync(id, Ct))!.Status.Should().Be(WorkflowStatus.Compensated);
        (await store.GetGroupFanOutAsync(id, "items", Ct)).Should().BeEquivalentTo(before);
        (await CountAsync("workflow_group_fanout_closures")).Should().Be(1);
    }

    [Fact]
    public async Task CallerMutationBeforeNextClaim_RejectsChangedInputBeforeCallback()
    {
        var store = CreateStore();
        var calls = new List<string>();
        var registry = new WorkflowRegistry().Register(new WorkflowRegistration<string, string>(
            new WorkflowDefinition { Name = "mutable-fanout", Version = "1" }, () => new MutableWorkflow(calls), Manifest()));
        await using var engine = new WorkflowEngine(store, registry, new ZhinuOptions());
        var id = await engine.StartAsync("mutable-fanout", "1", "input", cancellationToken: Ct);
        await engine.ExecuteAsync(id, Ct);
        (await store.GetRunAsync(id, Ct))!.Status.Should().Be(WorkflowStatus.Failed);
        calls.Should().Equal("a");
        (await store.GetStepsAsync(id, Ct)).Select(step => step.StepKey).Should().NotContain("items.b");
        (await store.GetGroupFanOutAsync(id, "items", Ct))!.IsComplete.Should().BeFalse();
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("missing-selection")]
    [InlineData("missing-dependency")]
    public async Task EmptyAndInvalidBoundaries_AreExplicit(string mode)
    {
        var store = CreateStore();
        var workflow = new FanWorkflow(Variant.FunctionalKeyed) { Change = mode };
        await using var engine = Engine(store, workflow);
        var id = await engine.StartAsync("fan-groups", "1", "input", cancellationToken: Ct);
        await engine.ExecuteAsync(id, Ct);
        workflow.Calls.Should().BeEmpty();
        if (mode == "empty")
        {
            (await store.GetRunAsync(id, Ct))!.Status.Should().Be(WorkflowStatus.Completed);
            var state = await store.GetGroupFanOutAsync(id, "items", Ct);
            state!.ItemCount.Should().Be(0);
            state.IsComplete.Should().BeTrue();
        }
        else
        {
            (await store.GetRunAsync(id, Ct))!.Status.Should().Be(WorkflowStatus.Failed);
            (await store.GetGroupFanOutAsync(id, "items", Ct)).Should().BeNull();
            (await store.GetRunAsync(id, Ct))!.GroupFanOutUsed.Should().BeFalse();
        }
    }

    [Fact]
    public async Task MissingFanOutCapability_FailsBeforeSelectorAndOnMarkedResume()
    {
        var store = CreateStore();
        var workflow = new FanWorkflow(Variant.FunctionalKeyed);
        var hidden = DispatchProxy.Create<IFoundationStore, FoundationProxy>();
        ((FoundationProxy)(object)hidden).Backing = store;
        await using (var unsupported = Engine(hidden, workflow))
        {
            var id = await unsupported.StartAsync("fan-groups", "1", "input", cancellationToken: Ct);
            await unsupported.ExecuteAsync(id, Ct);
            (await store.GetRunAsync(id, Ct))!.Status.Should().Be(WorkflowStatus.Failed);
            workflow.SelectorCalls.Should().Be(0);
            workflow.Calls.Should().BeEmpty();
        }
        await using var supported = Engine(store, workflow);
        var completedId = await supported.StartAsync("fan-groups", "1", "input", cancellationToken: Ct);
        await supported.ExecuteAsync(completedId, Ct);
        await using var resume = Engine(hidden, workflow);
        var act = () => resume.ExecuteAsync(completedId, Ct);
        await act.Should().ThrowAsync<WorkflowConfigurationException>();
    }

    private WorkflowEngine Engine(IWorkflowStore store, FanWorkflow workflow) => new(store,
        new WorkflowRegistry().Register(new WorkflowRegistration<string, string>(
            new WorkflowDefinition { Name = "fan-groups", Version = "1" }, () => workflow, Manifest())),
        new ZhinuOptions { PollInterval = TimeSpan.FromMilliseconds(10) }, null, null, null, null, new Resolver(workflow));

    private static ActivityGroupManifest Manifest() => ActivityGroupManifest.Create([
        new ActivityGroupDefinition("root", ActivityGroupKind.Logical, "Root"),
        new ActivityGroupDefinition("work", ActivityGroupKind.Macro, "Work", "root", provenance:
            new ActivityGroupMacroProvenance("AnyMacro", "pin", "invocation"))]);

    private async Task<int> CountAsync(string table)
    {
        await using var connection = new SqliteConnection($"Data Source={Path.Combine(root, "zhinu.db")};Pooling=False");
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM " + table;
        return Convert.ToInt32(await command.ExecuteScalarAsync(Ct));
    }

    public enum Variant { FunctionalKeyed, FunctionalIndex, ClassKeyed, ClassIndex }

    private sealed class ConcurrentWorkflow(TaskCompletionSource entered, TaskCompletionSource release) : IWorkflow<string, string>
    {
        private int callbacks;
        public async Task<string> RunAsync(WorkflowContext context, string input, CancellationToken ct)
        {
            var selected = await context.StepAsync("select", input, (_, _) => Task.FromResult(new[] { "a", "b" }), cancellationToken: ct);
            var result = await context.FanOutAsync("items", selected, value => value, async (value, _, token) =>
            {
                if (Interlocked.Increment(ref callbacks) == 2) entered.TrySetResult();
                await release.Task.WaitAsync(token);
                return value;
            }, new FanOutOptions { MaxDegreeOfParallelism = 2 }, new StepOptions
            {
                DependsOn = ["select"],
                FanOutGroup = new ActivityGroupFanOutBinding(new ActivityGroupMembership("work"), "select", context.GetActivityGroup("root"))
            }, ct);
            return string.Join(':', result);
        }
    }

    private sealed class FanWorkflow(Variant variant) : IWorkflow<string, string>
    {
        public string? Change { get; set; }
        public bool Fail { get; init; }
        public bool Swallow { get; init; }
        public bool RetryOnce { get; init; }
        public StepCompensationMode Compensation { get; init; }
        public List<string> Compensated { get; } = [];
        public int SelectorCalls;
        public ConcurrentDictionary<string, int> Calls { get; } = new(StringComparer.Ordinal);

        public Task<string> Body(string value)
        {
            var calls = Calls.AddOrUpdate(value, 1, (_, count) => count + 1);
            if (value == "a" && (Fail || (RetryOnce && calls == 1))) throw new InvalidOperationException("body failure");
            return Task.FromResult(value.ToUpperInvariant());
        }

        public async Task<string> RunAsync(WorkflowContext context, string input, CancellationToken ct)
        {
            var selected = Change == "missing-selection" ? new[] { "a", "b" } :
                await context.StepAsync("select", input, (_, _) => Task.FromResult(new[] { "a", "b" }), cancellationToken: ct);
            selected = Change switch
            {
                "reverse" or "positional-reverse" => Enumerable.Reverse(selected).ToArray(),
                "changed" => ["a!", "b"],
                "add" => ["a", "b", "c"],
                "remove" => ["a"],
                "empty" => [],
                _ => selected
            };
            var options = new StepOptions
            {
                FanOutGroup = new ActivityGroupFanOutBinding(new ActivityGroupMembership("work"), "select", context.GetActivityGroup("root")),
                DependsOn = Change == "missing-dependency" ? [] : ["select"],
                Retry = new RetryPolicy { MaxAttempts = RetryOnce ? 2 : 1 }
            };
            string Key(string value) { Interlocked.Increment(ref SelectorCalls); return value[..1]; }
            IReadOnlyList<string> results;
            try
            {
                results = variant switch
                {
                    Variant.FunctionalKeyed => await context.FanOutAsync("items", selected, Key,
                        (value, _, _) => Body(value), new FanOutOptions { MaxDegreeOfParallelism = 1 }, options, ct),
                    Variant.FunctionalIndex => await context.FanOutAsync("items", selected, (value, _, _) => Body(value), options, ct),
                    Variant.ClassKeyed => await context.FanOutAsync("items", new WorkflowStepReference<string, string>(new StepImplementationKey("item")),
                        selected, Key, new FanOutOptions { MaxDegreeOfParallelism = 1 }, options, ct, Compensation),
                    _ => await context.FanOutAsync("items", new WorkflowStepReference<string, string>(new StepImplementationKey("item")), selected, options, ct, Compensation)
                };
            }
            catch (Exception) when (Swallow) { return "swallowed"; }
            return await context.StepAsync("finish", string.Join(':', results), (value, _) => Task.FromResult(value), cancellationToken: ct);
        }
    }

    private sealed class Resolver(FanWorkflow workflow) : IWorkflowStepResolver
    {
        public ValueTask<IWorkflowStepLease<TStep>> ResolveAsync<TStep>(StepImplementationKey key, CancellationToken ct) where TStep : class =>
            ValueTask.FromResult<IWorkflowStepLease<TStep>>(new Lease<TStep>((TStep)(object)new ItemStep(workflow)));
    }
    private sealed class Lease<TStep>(TStep step) : IWorkflowStepLease<TStep> where TStep : class
    {
        public TStep Step => step;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class ItemStep(FanWorkflow workflow) : ICompensatingWorkflowStep<string, string>
    {
        public Task<string> ExecuteAsync(WorkflowStepContext context, string input, CancellationToken ct) => workflow.Body(input);
        public Task CompensateAsync(WorkflowStepContext context, string input, string output, CancellationToken ct)
        {
            workflow.Compensated.Add(input + ":" + output);
            return Task.CompletedTask;
        }
    }

    public sealed class MutableInput
    {
        public string Key { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
    }

    private sealed class MutableWorkflow(List<string> calls) : IWorkflow<string, string>
    {
        public async Task<string> RunAsync(WorkflowContext context, string input, CancellationToken ct)
        {
            var selected = await context.StepAsync("select", input, (_, _) => Task.FromResult(new[]
            {
                new MutableInput { Key = "a", Value = "first" }, new MutableInput { Key = "b", Value = "second" }
            }), cancellationToken: ct);
            await context.FanOutAsync("items", selected, item => item.Key, (item, _, _) =>
            {
                calls.Add(item.Key);
                selected[1].Value = "mutated";
                return Task.FromResult(item.Value);
            }, new FanOutOptions { MaxDegreeOfParallelism = 1 }, new StepOptions
            {
                DependsOn = ["select"],
                FanOutGroup = new ActivityGroupFanOutBinding(new ActivityGroupMembership("work"), "select", context.GetActivityGroup("root"))
            }, ct);
            return "unexpected";
        }
    }
    public interface IFoundationStore : IWorkflowStore, IWorkflowGroupRepository { }
    public class FoundationProxy : DispatchProxy
    {
        public SqliteWorkflowStore Backing { get; set; } = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            try { return method!.Invoke(Backing, args); }
            catch (TargetInvocationException error) when (error.InnerException is not null)
            { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
        }
    }
}
