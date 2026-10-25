using System.Reflection;
using System.Runtime.ExceptionServices;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;

namespace Penghou.Zhinu.Sqlite.ProcessProbe;

public static partial class Program
{
    private static async Task<int> GroupForkProbeAsync(string databasePath, Guid sourceId, Guid destinationId, string mode)
    {
        if (mode is not ("manifest" or "membership" or "event" or "committed" or "running" or "member-completed" or "joined"))
            throw new ArgumentException("Unknown group-fork probe mode.", nameof(mode));
        var database = new SqliteDatabase(new ZhinuSqliteOptions
        {
            DatabasePath = databasePath,
            BusyTimeout = TimeSpan.FromSeconds(5),
            Pooling = false
        });
        await database.InitializeAsync().ConfigureAwait(false);
        if (mode is "manifest" or "membership" or "event")
        {
            var table = mode switch
            {
                "manifest" => "workflow_group_manifests",
                "membership" => "workflow_step_group_memberships",
                _ => "workflow_events"
            };
            await using var connection = await database.OpenAsync(CancellationToken.None).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                CREATE TRIGGER zhinu_hard_exit_group_fork
                BEFORE INSERT ON {table}
                WHEN NEW.workflow_run_id='{destinationId:D}'
                BEGIN SELECT zhinu_hard_exit(); END;
                """;
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        var store = new SqliteWorkflowStore(new HardExitSqliteDatabase(database));
        IWorkflowStore selected = store;
        if (mode == "committed")
        {
            var proxy = DispatchProxy.Create<IForkProbeStore, ForkProbeStoreProxy>();
            ((ForkProbeStoreProxy)(object)proxy).Store = store;
            selected = proxy;
        }
        await using var engine = new WorkflowEngine(selected, GroupForkWorkflow.Registry(new GroupForkWorkflow(mode)), new ZhinuOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(10),
            LeaseDuration = TimeSpan.FromSeconds(2),
            LeaseRenewalInterval = TimeSpan.FromMilliseconds(600)
        });
        await engine.ForkAsync(sourceId, "work", new ForkRunOptions { WorkflowRunId = destinationId }, CancellationToken.None)
            .ConfigureAwait(false);
        await engine.ExecuteAsync(destinationId, CancellationToken.None).ConfigureAwait(false);
        return 0;
    }

    public interface IForkProbeStore : IWorkflowStore, IWorkflowGroupRepository, IWorkflowGroupForkRepository { }
    public class ForkProbeStoreProxy : DispatchProxy
    {
        public SqliteWorkflowStore Store { get; set; } = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            try
            {
                var value = targetMethod!.Invoke(Store, args);
                return targetMethod.DeclaringType == typeof(IWorkflowGroupForkRepository)
                    ? ExitAfterCommitAsync((ValueTask<ForkPlan>)value!) : value;
            }
            catch (TargetInvocationException exception) when (exception.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
                throw;
            }
        }

        private static async ValueTask<ForkPlan> ExitAfterCommitAsync(ValueTask<ForkPlan> operation)
        {
            var result = await operation.ConfigureAwait(false);
            Environment.Exit(93);
            return result;
        }
    }
}
