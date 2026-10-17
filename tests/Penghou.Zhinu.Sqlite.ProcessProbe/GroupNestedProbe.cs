using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;

namespace Penghou.Zhinu.Sqlite.ProcessProbe;

public static partial class Program
{
    private static async Task<int> GroupNestedProbeAsync(string databasePath, string runIdPath, string mode)
    {
        if (mode is not ("outer-ready" or "inner-ready" or "early-running" or "later-running" or
            "leaf-completed" or "inner-completed" or "outer-committed" or "recover"))
            throw new ArgumentException("Unknown nested repetition probe boundary.", nameof(mode));
        var store = new SqliteWorkflowStore(new ZhinuSqliteOptions { DatabasePath = databasePath, Pooling = false });
        IWorkflowStore selected = store;
        if (mode == "outer-committed")
        {
            var proxy = DispatchProxy.Create<INestedProbeStore, NestedProbeStoreProxy>();
            ((NestedProbeStoreProxy)(object)proxy).Store = store;
            selected = proxy;
        }
        var calls = new List<GroupNestedCall>();
        var recoveredAt = DateTimeOffset.UtcNow.AddMinutes(1);
        await using var engine = new WorkflowEngine(selected, GroupNestedWorkflow.Registry(new GroupNestedWorkflow(mode, calls.Add)),
            new ZhinuOptions
            {
                PollInterval = TimeSpan.FromMilliseconds(10),
                LeaseDuration = TimeSpan.FromSeconds(2),
                LeaseRenewalInterval = TimeSpan.FromMilliseconds(600)
            }, timeProvider: mode == "recover" ? new NestedRecoveryClock(recoveredAt) : TimeProvider.System);
        Guid id;
        if (mode == "recover")
        {
            id = Guid.Parse(await File.ReadAllTextAsync(runIdPath).ConfigureAwait(false));
            // Explicit lease recovery avoids timing/sleep barriers. The first
            // worker is already dead; no concurrent owner can extend this lease.
            await store.RecoverExpiredLeasesAsync(recoveredAt).ConfigureAwait(false);
        }
        else
        {
            id = await engine.StartAsync(GroupNestedWorkflow.Name, "1", "identical-input").ConfigureAwait(false);
            await File.WriteAllTextAsync(runIdPath, id.ToString("D")).ConfigureAwait(false);
        }
        await engine.ExecuteAsync(id).ConfigureAwait(false);
        var result = await engine.WaitForCompletionAsync<string>(id).ConfigureAwait(false);
        await File.WriteAllTextAsync(runIdPath + ".recovery", JsonSerializer.Serialize(new GroupNestedRecovery(result, calls)))
            .ConfigureAwait(false);
        return mode == "recover" ? 0 : 4; // A crash mode must reach its selected boundary.
    }

    private sealed class NestedRecoveryClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    public interface INestedProbeStore : IWorkflowStore, IWorkflowGroupRepository { }
    public class NestedProbeStoreProxy : DispatchProxy
    {
        public SqliteWorkflowStore Store { get; set; } = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            try
            {
                var value = targetMethod!.Invoke(Store, args);
                return targetMethod.Name == nameof(IWorkflowStore.CompleteStepWithEventsAsync)
                    ? AfterCompletionAsync((ValueTask<IReadOnlyList<WorkflowEvent>>)value!) : value;
            }
            catch (TargetInvocationException error) when (error.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(error.InnerException).Throw();
                throw;
            }
        }

        private static async ValueTask<IReadOnlyList<WorkflowEvent>> AfterCompletionAsync(ValueTask<IReadOnlyList<WorkflowEvent>> pending)
        {
            var events = await pending.ConfigureAwait(false);
            if (events.Any(item => item.StepKey == "$loop/outer/1/commit")) Environment.Exit(93);
            return events;
        }
    }
}

