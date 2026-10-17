using System.Reflection;
using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;

namespace Penghou.Zhinu.Sqlite.ProcessProbe;

public static partial class Program
{
    private static async Task<int> GroupSelectionProbeAsync(string databasePath, string runIdPath, string mode)
    {
        if (mode is not ("selection" or "admit-transaction" or "admitted" or "contracted-selection" or
            "partial" or "obligations-completed" or "reordered" or "reappeared" or "recover" or "recover-omit"))
            throw new ArgumentException("Unknown evolving selection probe boundary.", nameof(mode));
        var database = new SqliteDatabase(new ZhinuSqliteOptions { DatabasePath = databasePath, Pooling = false });
        await database.InitializeAsync().ConfigureAwait(false);
        await using (var connection = await database.OpenAsync(CancellationToken.None).ConfigureAwait(false))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = mode == "admit-transaction" ? """
                CREATE TRIGGER zhinu_selection_exit BEFORE INSERT ON workflow_group_fanout_items
                WHEN NEW.item_key='C' AND EXISTS (
                    SELECT 1 FROM workflow_group_fanout_expansions e
                    WHERE e.expansion_id=NEW.expansion_id AND e.fan_out_key='items.2')
                BEGIN SELECT zhinu_hard_exit(); END;
                """ : "DROP TRIGGER IF EXISTS zhinu_selection_exit;";
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        var store = new SqliteWorkflowStore(new HardExitSqliteDatabase(database));
        IWorkflowStore selected = store;
        if (mode == "admitted")
        {
            var proxy = DispatchProxy.Create<ISelectionProbeStore, SelectionProbeProxy>();
            ((SelectionProbeProxy)(object)proxy).Store = store;
            selected = proxy;
        }
        var calls = new ConcurrentQueue<SelectionCall>();
        var selectors = new List<int>();
        var schedules = new List<int[]>();
        var recovering = mode is "recover" or "recover-omit";
        var recoveredAt = DateTimeOffset.UtcNow.AddMinutes(1);
        await using var engine = new WorkflowEngine(selected, GroupSelectionWorkflow.Registry(new GroupSelectionWorkflow(mode)
        {
            CurrentSelectors = recovering,
            OmitFirstEpoch = mode == "recover-omit",
            OnCall = calls.Enqueue,
            OnSelection = selectors.Add,
            OnSchedule = schedules.Add
        }), new ZhinuOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(10),
            LeaseDuration = TimeSpan.FromSeconds(2),
            LeaseRenewalInterval = TimeSpan.FromMilliseconds(600)
        }, timeProvider: recovering ? new NestedRecoveryClock(recoveredAt) : TimeProvider.System);
        Guid id;
        if (recovering)
        {
            id = Guid.Parse(await File.ReadAllTextAsync(runIdPath).ConfigureAwait(false));
            await store.RecoverExpiredLeasesAsync(recoveredAt).ConfigureAwait(false);
        }
        else
        {
            id = await engine.StartAsync(GroupSelectionWorkflow.Name, "1", "stable-schedule").ConfigureAwait(false);
            await File.WriteAllTextAsync(runIdPath, id.ToString("D")).ConfigureAwait(false);
        }
        await engine.ExecuteAsync(id).ConfigureAwait(false);
        if (mode == "recover-omit")
        {
            var run = (await store.GetRunAsync(id).ConfigureAwait(false))!;
            await File.WriteAllTextAsync(runIdPath + ".omission", JsonSerializer.Serialize(new SelectionOmission(
                run.Status, run.Error?.Type, run.Error?.Message, calls.ToArray()))).ConfigureAwait(false);
            return run.Status == WorkflowStatus.Failed ? 0 : 5;
        }
        var result = await engine.WaitForCompletionAsync<string>(id).ConfigureAwait(false);
        await File.WriteAllTextAsync(runIdPath + ".recovery", JsonSerializer.Serialize(new SelectionRecovery(result, selectors, schedules, calls.ToArray())))
            .ConfigureAwait(false);
        return mode == "recover" ? 0 : 4;
    }

    public interface ISelectionProbeStore : IWorkflowStore, IWorkflowGroupRepository, IWorkflowGroupFanOutRepository { }
    public class SelectionProbeProxy : DispatchProxy
    {
        public SqliteWorkflowStore Store { get; set; } = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            try
            {
                var value = targetMethod!.Invoke(Store, args);
                return targetMethod.Name == nameof(IWorkflowGroupFanOutRepository.AdmitGroupFanOutAsync)
                    ? AfterAdmissionAsync((ValueTask<ActivityGroupFanOutState>)value!) : value;
            }
            catch (TargetInvocationException error) when (error.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(error.InnerException).Throw();
                throw;
            }
        }

        private static async ValueTask<ActivityGroupFanOutState> AfterAdmissionAsync(ValueTask<ActivityGroupFanOutState> pending)
        {
            var admitted = await pending.ConfigureAwait(false);
            if (admitted.FanOutKey == GroupSelectionWorkflow.Scope(2)) Environment.Exit(93);
            return admitted;
        }
    }
}
