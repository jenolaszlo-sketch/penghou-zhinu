using Penghou.Zhinu;

namespace Penghou.Zhinu.Sqlite.ProcessProbe;

// String contracts keep durable type identities identical in both assemblies.
internal static class GroupChildWorkflow
{
    internal const string ParentName = "group-child-parent";
    internal const string ChildName = "group-child-leaf";

    internal static ActivityGroupManifest Manifest() => ActivityGroupManifest.Create([
        new ActivityGroupDefinition("root", ActivityGroupKind.Logical, "Root"),
        new ActivityGroupDefinition("macro", ActivityGroupKind.Macro, "Macro", "root", provenance:
            new ActivityGroupMacroProvenance("Macro", "child-boundary", "call"))]);

    internal static WorkflowRegistry Registry(Action<WorkflowStepContext>? childCallback = null) => new WorkflowRegistry()
        .Register(new WorkflowRegistration<string, string>(
            new WorkflowDefinition { Name = ParentName, Version = "1" }, () => new Parent(), Manifest()))
        .Register(new WorkflowRegistration<string, string>(
            new WorkflowDefinition { Name = ChildName, Version = "1" }, () => new Child(childCallback), Manifest()));

    internal static ActivityGroupStepBinding Binding(WorkflowContext context, ActivityGroupMemberRole role = ActivityGroupMemberRole.Work)
    {
        var root = context.GetActivityGroup("root");
        return new ActivityGroupStepBinding(new ActivityGroupMembership("macro", role), context.GetActivityGroup("macro", root));
    }

    private sealed class Parent : IWorkflow<string, string>
    {
        public async Task<string> RunAsync(WorkflowContext context, string input, CancellationToken cancellationToken)
        {
            var result = await context.StartChildAsync<string, string>("child", ChildName, "1", input, new ChildRunOptions
            {
                StartGroup = Binding(context, ActivityGroupMemberRole.Control),
                WaitGroup = Binding(context)
            }, cancellationToken).ConfigureAwait(false);
            return await context.StepAsync("tail", result, (value, _) => Task.FromResult(value), cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private sealed class Child(Action<WorkflowStepContext>? callback) : IWorkflow<string, string>
    {
        public Task<string> RunAsync(WorkflowContext context, string input, CancellationToken cancellationToken) =>
            context.StepAsync("work", input, (value, step, _) =>
            {
                callback?.Invoke(step);
                return Task.FromResult(value + ":child");
            }, new StepOptions { Group = Binding(context), Retry = new RetryPolicy { MaxAttempts = 2 } }, cancellationToken);
    }
}
