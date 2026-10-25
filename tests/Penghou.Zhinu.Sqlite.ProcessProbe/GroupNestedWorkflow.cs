using Penghou.Zhinu;

namespace Penghou.Zhinu.Sqlite.ProcessProbe;

// One definition shared by in-process tests and both fresh workers. Selection is
// fixed at 3 x 2; every leaf deliberately receives the same input.
internal sealed class GroupNestedWorkflow(string? mode = null, Action<GroupNestedCall>? callback = null,
    string? retryStep = null) : IWorkflow<string, string>
{
    internal const string Name = "process-group-nested";
    internal static ActivityGroupManifest Manifest() => ActivityGroupManifest.Create([
        new ActivityGroupDefinition("outer", ActivityGroupKind.Macro, "Outer", provenance:
            new ActivityGroupMacroProvenance("Outer", "fixed-three", "outer-call")),
        new ActivityGroupDefinition("inner", ActivityGroupKind.Macro, "Inner", "outer", provenance:
            new ActivityGroupMacroProvenance("Inner", "fixed-two", "inner-call")),
        new ActivityGroupDefinition("leaf", ActivityGroupKind.Logical, "Leaf", "inner")]);

    internal static WorkflowRegistry Registry(GroupNestedWorkflow workflow) => new WorkflowRegistry().Register(
        new WorkflowRegistration<string, string>(new WorkflowDefinition { Name = Name, Version = "1" }, () => workflow, Manifest()));

    public async Task<string> RunAsync(WorkflowContext context, string input, CancellationToken cancellationToken)
    {
        var result = await context.LoopAsync("outer", 0, state => state < 3, async (outer, ct) =>
        {
            var outerGroup = outer.GetActivityGroup("outer");
            await outer.StepAsync("entry", input, (value, step, _) =>
            {
                Call(step, outerGroup);
                return Task.FromResult(value);
            }, Options(outerGroup), ct).ConfigureAwait(false);
            Exit("outer-ready", outer.Iteration == 1);
            var innerResult = await outer.LoopAsync("inner", 0, state => state < 2, async (inner, innerCt) =>
            {
                var innerGroup = inner.GetActivityGroup("inner", outerGroup);
                await inner.StepAsync("entry", input, (value, step, _) =>
                {
                    Call(step, innerGroup);
                    Exit("inner-ready", outer.Iteration == 1 && inner.Iteration == 1);
                    return Task.FromResult(value);
                }, Options(innerGroup), innerCt).ConfigureAwait(false);
                var leaf = inner.GetActivityGroup("leaf", innerGroup);
                await inner.StepAsync("work", input, (value, step, _) =>
                {
                    Call(step, leaf);
                    Exit("early-running", outer.Iteration == 1 && inner.Iteration == 2);
                    Exit("later-running", outer.Iteration == 2 && inner.Iteration == 2);
                    return Task.FromResult(value);
                }, Options(leaf), innerCt).ConfigureAwait(false);
                Exit("leaf-completed", outer.Iteration == 1 && inner.Iteration == 1);
                return inner.Continue(inner.State + 1);
            }, new LoopOptions(maxIterations: 2), ct).ConfigureAwait(false);
            if (innerResult != 2) throw new InvalidOperationException("Inner completion lost its durable state.");
            Exit("inner-completed", outer.Iteration == 1);
            return outer.Continue(outer.State + 1);
        }, new LoopOptions(maxIterations: 3), cancellationToken).ConfigureAwait(false);
        return result.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private void Call(WorkflowStepContext step, ActivityGroupOccurrence group)
    {
        callback?.Invoke(new GroupNestedCall(step.StepKey, step.StepExecutionId, step.Revision, step.Attempt,
            group.GroupOccurrenceId, group.ParentGroupOccurrenceId, group.ExecutionCoordinate));
        if (step.StepKey == retryStep && step.Attempt == 1)
            throw new InvalidOperationException("Deterministic first-attempt failure.");
    }

    private static StepOptions Options(ActivityGroupOccurrence group) => new()
    {
        Group = new ActivityGroupStepBinding(new ActivityGroupMembership(group.GroupKey), group),
        Retry = new RetryPolicy { MaxAttempts = 2 }
    };

    private void Exit(string boundary, bool selected)
    {
        if (mode == boundary && selected) Environment.Exit(93);
    }
}

internal sealed record GroupNestedCall(string StepKey, Guid StepId, int Revision, int Attempt,
    string Occurrence, string? Parent, string Coordinate);

internal sealed record GroupNestedRecovery(string Result, IReadOnlyList<GroupNestedCall> Calls);
