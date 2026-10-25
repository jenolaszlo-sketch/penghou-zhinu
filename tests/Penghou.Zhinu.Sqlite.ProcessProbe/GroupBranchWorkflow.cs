using Penghou.Zhinu;

namespace Penghou.Zhinu.Sqlite.ProcessProbe;

internal sealed class GroupBranchWorkflow(string choice) : IWorkflow<string, string>
{
    internal TaskCompletionSource? Entered { get; init; }
    internal Action<string>? Barrier { get; init; }
    internal Action<string>? OnCall { get; init; }
    internal string? Replaces { get; init; } = "branch.a";
    internal string? ScopeKey { get; init; }

    internal static WorkflowRegistry Registry(GroupBranchWorkflow workflow) => new WorkflowRegistry().Register(
        new WorkflowRegistration<string, string>(new WorkflowDefinition { Name = "branch-replacement", Version = "1" },
            () => workflow, ActivityGroupManifest.Create([
                new ActivityGroupDefinition("root", ActivityGroupKind.Logical, "Root"),
                new ActivityGroupDefinition("branch", ActivityGroupKind.Logical, "Branch", "root"),
                new ActivityGroupDefinition("nested", ActivityGroupKind.Logical, "Nested", "branch")])));

    public async Task<string> RunAsync(WorkflowContext context, string input, CancellationToken cancellationToken)
    {
        var selected = await context.StepAsync("select", input, (_, _) => Task.FromResult(choice),
            new StepOptions { Group = new ActivityGroupStepBinding(new ActivityGroupMembership("root", ActivityGroupMemberRole.Control), context.GetActivityGroup("root")) }, cancellationToken);
        if (selected != "a") Barrier?.Invoke("selection");
        var result = await context.FanOutBodyAsync(ScopeKey ?? "branch." + selected, new[] { selected }, key => key,
            async (key, scope, ct) =>
            {
                var nested = scope.GetActivityGroup("nested");
                var group = new ActivityGroupStepBinding(new ActivityGroupMembership("nested"), nested);
                await scope.StepAsync("done", key, (_, step, _) =>
                {
                    OnCall?.Invoke(step.StepKey);
                    return Task.FromResult(key);
                }, options: new StepOptions { Group = group }, cancellationToken: ct);
                await scope.StepAsync("unfinished", key, async (_, step, token) =>
                {
                    OnCall?.Invoke(step.StepKey);
                    Barrier?.Invoke(selected == "a" ? "seed" : "partial");
                    if (selected == "a" && Entered is not null)
                    {
                        Entered.TrySetResult();
                        await Task.Delay(Timeout.Infinite, token);
                    }
                    return key;
                }, options: new StepOptions { Group = group }, cancellationToken: ct);
                return key;
            }, "branch-body-v1", new FanOutOptions { MaxDegreeOfParallelism = 1 }, new StepOptions
            {
                DependsOn = ["select"],
                FanOutGroup = new ActivityGroupFanOutBinding(new ActivityGroupMembership("branch", ActivityGroupMemberRole.Control),
                    "select", context.GetActivityGroup("root"))
                { ReplacesFanOutKey = selected != "a" || ScopeKey is not null ? Replaces : null }
            }, cancellationToken);
        return result.Single();
    }
}
