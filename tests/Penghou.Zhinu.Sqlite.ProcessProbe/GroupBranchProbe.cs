using System.Reflection;
using System.Runtime.ExceptionServices;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;

namespace Penghou.Zhinu.Sqlite.ProcessProbe;

public static partial class Program
{
    private static async Task<int> GroupBranchProbeAsync(string databasePath, string runPath, string mode)
    {
        if (mode is not ("seed" or "selection" or "transaction" or "admitted" or "partial" or "recover")) return 2;
        var database = new SqliteDatabase(new ZhinuSqliteOptions { DatabasePath = databasePath, Pooling = false });
        await database.InitializeAsync();
        await using (var connection = await database.OpenAsync(CancellationToken.None))
        await using (var command = connection.CreateCommand())
        {
            // Exit after cancellation has happened but before retirement's event or commit.
            command.CommandText = mode == "transaction" ? """
                CREATE TRIGGER zhinu_branch_exit AFTER UPDATE ON workflow_steps
                WHEN NEW.status=5 AND EXISTS (SELECT 1 FROM workflow_group_fanout_retirements)
                BEGIN SELECT zhinu_hard_exit(); END;
                """ : "DROP TRIGGER IF EXISTS zhinu_branch_exit;";
            await command.ExecuteNonQueryAsync();
        }
        var store = new SqliteWorkflowStore(new HardExitSqliteDatabase(database));
        IWorkflowStore selected = store;
        if (mode == "admitted")
        {
            var proxy = DispatchProxy.Create<IBranchProbeStore, BranchProbeProxy>();
            ((BranchProbeProxy)(object)proxy).Store = store;
            selected = proxy;
        }
        var now = DateTimeOffset.UtcNow.AddMinutes(mode == "recover" ? 2 : 1);
        var workflow = new GroupBranchWorkflow(mode == "seed" ? "a" : mode == "recover" ? "changed-today" : "b")
        {
            Barrier = boundary => { if (boundary == mode) Environment.Exit(93); },
            OnCall = key => File.AppendAllText(runPath + ".calls", key + "\n")
        };
        await using var engine = new WorkflowEngine(selected, GroupBranchWorkflow.Registry(workflow), new ZhinuOptions
        {
            LeaseDuration = TimeSpan.FromSeconds(2),
            LeaseRenewalInterval = TimeSpan.FromMilliseconds(600),
            PollInterval = TimeSpan.FromMilliseconds(10)
        }, timeProvider: mode == "seed" ? TimeProvider.System : new NestedRecoveryClock(now));
        Guid id;
        if (mode == "seed")
        {
            id = await engine.StartAsync("branch-replacement", "1", "input");
            await File.WriteAllTextAsync(runPath, id.ToString("D"));
        }
        else
        {
            id = Guid.Parse(await File.ReadAllTextAsync(runPath));
            await store.RecoverExpiredLeasesAsync(now);
            if (mode != "recover")
                await engine.RestartStepAsync(id, "select", new RestartStepOptions { Mode = StepRestartMode.StepOnly });
        }
        await engine.ExecuteAsync(id);
        var result = await engine.WaitForCompletionAsync<string>(id);
        await File.WriteAllTextAsync(runPath + ".result", result);
        return result == "b" ? 0 : 4;
    }

    public interface IBranchProbeStore : IWorkflowStore, IWorkflowGroupRepository,
        IWorkflowGroupFanOutBodyRepository, IWorkflowGroupFanOutReplacementRepository
    { }

    public class BranchProbeProxy : DispatchProxy
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
            var admitted = await pending;
            if (admitted.FanOutKey == "branch.b") Environment.Exit(93);
            return admitted;
        }
    }
}
