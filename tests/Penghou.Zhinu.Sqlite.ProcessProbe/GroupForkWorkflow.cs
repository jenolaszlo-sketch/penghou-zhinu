using Penghou.Zhinu;

namespace Penghou.Zhinu.Sqlite.ProcessProbe;

// Linked into the SQLite tests as well as the worker so recovery executes the
// identical definition after process loss, with only test receipts injected.
internal sealed class GroupForkWorkflow(string? mode = null, Action<string>? callback = null) : IWorkflow<string, string>
{
    internal const string Name = "process-group-fork";
    internal static ActivityGroupManifest Manifest() => ActivityGroupManifest.Create([
        new ActivityGroupDefinition("root", ActivityGroupKind.Logical, "Root"),
        new ActivityGroupDefinition("macro", ActivityGroupKind.Macro, "Macro", "root", provenance:
            new ActivityGroupMacroProvenance("AnyMacro", "definition-pin", "invocation")),
        new ActivityGroupDefinition("leaf", ActivityGroupKind.Logical, "Leaf", "macro")]);

    internal static WorkflowRegistry Registry(GroupForkWorkflow workflow) => new WorkflowRegistry().Register(
        new WorkflowRegistration<string, string>(new WorkflowDefinition { Name = Name, Version = "1" }, () => workflow, Manifest()));

    public async Task<string> RunAsync(WorkflowContext context, string input, CancellationToken cancellationToken)
    {
        var root = context.GetActivityGroup("root");
        var macro = context.GetActivityGroup("macro", root);
        var leaf = context.GetActivityGroup("leaf", macro);
        var binding = new ActivityGroupStepBinding(new ActivityGroupMembership("leaf"), leaf);
        var seed = await context.StepAsync("seed", input, (value, _, _) =>
        {
            callback?.Invoke("seed");
            return Task.FromResult("seed:" + value);
        }, new StepOptions { Group = binding }, cancellationToken).ConfigureAwait(false);
        var plain = await context.StepAsync("plain", input, (value, _, _) =>
        {
            callback?.Invoke("plain");
            return Task.FromResult("plain:" + value);
        }, cancellationToken: cancellationToken).ConfigureAwait(false);
        var work = await context.StepAsync("work", seed, (value, _, _) =>
        {
            callback?.Invoke("work");
            if (mode == "running") Environment.Exit(93);
            return Task.FromResult(value + ":work");
        }, new StepOptions { DependsOn = ["seed", "plain"], Group = binding }, cancellationToken).ConfigureAwait(false);
        if (mode == "member-completed") Environment.Exit(93);
        var joined = await context.StepAsync("join", work + "|" + plain, (value, _, _) =>
        {
            callback?.Invoke("join");
            return Task.FromResult(value);
        }, new StepOptions { DependsOn = ["seed", "plain", "work"] }, cancellationToken).ConfigureAwait(false);
        if (mode == "joined") Environment.Exit(93);
        return joined;
    }
}
