using System.Globalization;
using Penghou.Zhinu;

namespace Penghou.Zhinu.Sqlite.ProcessProbe;

// Explicit root epochs, not a loop/fan-out composition or in-place plan mutation.
// Both workers link this definition. Gate callbacks expose genuinely unfinished
// older obligations while a later selection excludes them, without sleeps.
internal sealed class GroupSelectionWorkflow(string? mode = null) : IWorkflow<string, string>
{
    internal const string Name = "process-group-selection";
    internal bool CurrentSelectors { get; init; }
    internal bool Positional { get; init; }
    internal bool OmitFirstEpoch { get; init; }
    internal string? RetryStep { get; init; }
    internal Action<SelectionCall>? OnCall { get; init; }
    internal Action<int>? OnSelection { get; init; }
    internal Action<int[]>? OnSchedule { get; init; }
    internal Func<int, SelectionItem[]>? SelectionOverride { get; init; }

    internal static ActivityGroupManifest Manifest() => ActivityGroupManifest.Create([
        new ActivityGroupDefinition("root", ActivityGroupKind.Logical, "Root"),
        new ActivityGroupDefinition("work", ActivityGroupKind.Macro, "Work", "root", provenance:
            new ActivityGroupMacroProvenance("SelectedItem", "stable-definition", "epoch-items"))]);

    internal static WorkflowRegistry Registry(GroupSelectionWorkflow workflow) => new WorkflowRegistry().Register(
        new WorkflowRegistration<string, string>(new WorkflowDefinition { Name = Name, Version = "1" }, () => workflow, Manifest()));

    internal static string Scope(int epoch) => "items." + epoch.ToString(CultureInfo.InvariantCulture);
    internal static string Selection(int epoch) => "select." + epoch.ToString(CultureInfo.InvariantCulture);
    internal static SelectionItem[] Items(int epoch, bool current = false)
    {
        string[] keys = epoch switch
        {
            1 => ["A", "B", "C"],
            2 => ["A", "C", "D", "E"],
            3 => ["C", "A"],
            4 => ["A", "C"],
            5 => ["B", "A", "C"],
            _ => throw new ArgumentOutOfRangeException(nameof(epoch))
        };
        return (current ? Enumerable.Reverse(keys).Append("today-" + epoch.ToString(CultureInfo.InvariantCulture)) : keys)
            .Select(key => new SelectionItem(key, "identical-payload")).ToArray();
    }

    public async Task<string> RunAsync(WorkflowContext context, string input, CancellationToken cancellationToken)
    {
        // Replays pin the epoch identities themselves, not just the item lists.
        var schedule = await context.StepAsync("epoch-schedule", input, (_, _, _) =>
        {
            var value = CurrentSelectors ? new[] { 5, 4, 3, 2, 1 } : [1, 2, 3, 4, 5];
            OnSchedule?.Invoke(value);
            return Task.FromResult(value);
        }, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!schedule.SequenceEqual([1, 2, 3, 4, 5]))
            throw new InvalidOperationException("A recovered schedule was recomputed instead of replayed.");

        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = OmitFirstEpoch ? Task.FromResult<IReadOnlyList<string>>([]) : EpochAsync(schedule[0]);
        Task<IReadOnlyList<string>>? second = null;
        try
        {
            await ReachedOrCompletedAsync(firstStarted.Task, first).ConfigureAwait(false);
            second = EpochAsync(schedule[1]);
            await ReachedOrCompletedAsync(secondStarted.Task, second).ConfigureAwait(false);
            await EpochAsync(schedule[2]).ConfigureAwait(false);
            Exit("partial"); // Epoch 3 closed, old C/D still Running, old E unclaimed.
        }
        finally
        {
            release.TrySetResult();
            // Never leave sibling callbacks running when an epoch fails/retries.
            try { await first.ConfigureAwait(false); }
            finally { if (second is not null) await second.ConfigureAwait(false); }
        }
        Exit("obligations-completed");
        await EpochAsync(schedule[3]).ConfigureAwait(false);
        Exit("reordered");
        await EpochAsync(schedule[4]).ConfigureAwait(false);
        Exit("reappeared");
        return "five-epochs-complete";

        async Task<IReadOnlyList<string>> EpochAsync(int epoch)
        {
            var selected = await context.StepAsync(Selection(epoch), epoch, (value, _, _) =>
            {
                OnSelection?.Invoke(value);
                return Task.FromResult(SelectionOverride?.Invoke(value) ?? Items(value, CurrentSelectors));
            }, new StepOptions { DependsOn = ["epoch-schedule"] }, cancellationToken).ConfigureAwait(false);
            if (epoch == 2) Exit("selection");
            if (epoch == 3) Exit("contracted-selection");
            var options = new StepOptions
            {
                DependsOn = [Selection(epoch)],
                FanOutGroup = new ActivityGroupFanOutBinding(new ActivityGroupMembership("work"), Selection(epoch), context.GetActivityGroup("root")),
                Retry = new RetryPolicy { MaxAttempts = 3 }
            };
            var results = Positional
                ? await context.FanOutAsync(Scope(epoch), selected, ExecuteAsync, options, cancellationToken).ConfigureAwait(false)
                : await context.FanOutAsync(Scope(epoch), selected, item => item.Key, ExecuteAsync,
                    new FanOutOptions { MaxDegreeOfParallelism = 1 }, options, cancellationToken).ConfigureAwait(false);
            if (!results.SequenceEqual(selected.Select(item => item.Key + ":" + item.Payload), StringComparer.Ordinal))
                throw new InvalidOperationException("Epoch result order no longer follows its durable selection output.");
            return results;

            async Task<string> ExecuteAsync(SelectionItem item, WorkflowStepContext step, CancellationToken ct)
            {
                OnCall?.Invoke(new SelectionCall(epoch, item, step.StepKey, step.StepExecutionId, step.Revision, step.Attempt));
                if (step.StepKey == RetryStep && step.Attempt == 1)
                    throw new InvalidOperationException("Deterministic epoch-member retry.");
                if (epoch == 1 && item.Key == "C")
                {
                    firstStarted.TrySetResult();
                    await release.Task.WaitAsync(ct).ConfigureAwait(false);
                }
                if (epoch == 2 && item.Key == "D")
                {
                    secondStarted.TrySetResult();
                    await release.Task.WaitAsync(ct).ConfigureAwait(false);
                }
                return item.Key + ":" + item.Payload;
            }
        }
    }

    private static async Task ReachedOrCompletedAsync(Task reached, Task completed)
    {
        if (await Task.WhenAny(reached, completed).ConfigureAwait(false) == completed)
            await completed.ConfigureAwait(false);
    }

    private void Exit(string boundary)
    {
        if (mode == boundary) Environment.Exit(93);
    }
}

internal sealed record SelectionItem(string Key, string Payload);
internal sealed record SelectionCall(int Epoch, SelectionItem Input, string StepKey, Guid StepId, int Revision, int Attempt);
internal sealed record SelectionRecovery(string Result, IReadOnlyList<int> Selectors, IReadOnlyList<int[]> Schedules, IReadOnlyList<SelectionCall> Calls);
internal sealed record SelectionOmission(WorkflowStatus Status, string? ErrorType, string? ErrorMessage, IReadOnlyList<SelectionCall> Calls);
