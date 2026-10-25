using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;

namespace Penghou.Zhinu.Sqlite.ProcessProbe;

internal static class GroupChildProbe
{
    private const int HardExitCode = 93;

    public static async Task<int> RunAsync(
        string databasePath,
        Guid parentId,
        string mode,
        string receiptPath)
    {
        var options = new ZhinuSqliteOptions
        {
            DatabasePath = databasePath,
            BusyTimeout = TimeSpan.FromSeconds(5),
            Pooling = false
        };
        var raw = new SqliteWorkflowStore(options);
        var database = new SqliteDatabase(options);
        await database.InitializeAsync().ConfigureAwait(false);
        var childId = HashId($"{parentId:D}:child:1");
        var store = DispatchProxy.Create<IGroupChildProbeStore, GroupChildStoreProxy>();
        var proxy = (GroupChildStoreProxy)(object)store;
        proxy.Initialize(raw, raw, raw);

        if (mode is "parent-claim" or "child-manifest" or "child-created" or "wait-claim")
        {
            var (trigger, sql) = mode switch
            {
                "parent-claim" => ("zhinu_exit_parent_start_claim", """
                    CREATE TRIGGER zhinu_exit_parent_start_claim BEFORE INSERT ON workflow_step_group_memberships
                    WHEN NEW.workflow_run_id = '$PARENT' AND NEW.step_key = 'child:start'
                    BEGIN SELECT zhinu_hard_exit(); END;
                    """),
                "child-manifest" => ("zhinu_exit_child_manifest", """
                    CREATE TRIGGER zhinu_exit_child_manifest BEFORE INSERT ON workflow_group_manifests
                    WHEN NEW.workflow_run_id = '$CHILD'
                    BEGIN SELECT zhinu_hard_exit(); END;
                    """),
                "child-created" => ("zhinu_exit_child_generation", """
                    CREATE TRIGGER zhinu_exit_child_generation BEFORE INSERT ON workflow_generations
                    WHEN NEW.workflow_run_id = '$CHILD'
                    BEGIN SELECT zhinu_hard_exit(); END;
                    """),
                "wait-claim" => ("zhinu_exit_parent_wait_claim", """
                    CREATE TRIGGER zhinu_exit_parent_wait_claim BEFORE INSERT ON workflow_step_group_memberships
                    WHEN NEW.workflow_run_id = '$PARENT' AND NEW.step_key = 'child:wait'
                    BEGIN SELECT zhinu_hard_exit(); END;
                    """),
                _ => throw new InvalidOperationException()
            };
            await InstallTriggerAsync(database, trigger,
                sql.Replace("$PARENT", parentId.ToString("D"), StringComparison.Ordinal)
                   .Replace("$CHILD", childId.ToString("D"), StringComparison.Ordinal)).ConfigureAwait(false);
        }

        if (mode is "child-completed" or "peer-child-ready")
            proxy.ExitAfterCompleteRunId = childId;
        else if (mode == "wait-completed")
            proxy.ExitAfterStepOutput = JsonSerializer.Serialize("probe:child");
        else if (mode == "start-completed")
            proxy.ExitAfterStepOutput = JsonSerializer.Serialize(childId);

        if (mode == "peer-child-ready")
        {
            var peerEngine = new WorkflowEngine(
                store,
                GroupChildWorkflow.Registry(_ => BlockAsPeer(receiptPath)),
                new ZhinuOptions
                {
                    PollInterval = TimeSpan.FromMilliseconds(10),
                    LeaseDuration = TimeSpan.FromSeconds(30),
                    LeaseRenewalInterval = TimeSpan.FromSeconds(10)
                });
            File.WriteAllText(receiptPath, "peer-starting");
            await peerEngine.ExecuteAsync(childId, CancellationToken.None).ConfigureAwait(false);
            return 0;
        }

        WorkflowEngine engine;
        if (mode.StartsWith("recover", StringComparison.Ordinal))
        {
            var recoveryAt = await RecoverLeasesAsync(
                raw, parentId, childId, skipPeerChild: mode == "recover-parked-wait").ConfigureAwait(false);
            if (mode == "recover-parked-wait") proxy.ExitAfterParkChildId = childId;
            if (mode == "recover-wait-completed") proxy.ExitAfterStepOutput = JsonSerializer.Serialize("probe:child");
            engine = new WorkflowEngine(store,
                GroupChildWorkflow.Registry(_ => File.AppendAllText(receiptPath + ".callback", "child\n")),
                EngineOptions(),
                timeProvider: new FixedTimeProvider(recoveryAt));
            File.WriteAllText(receiptPath, "recovering");
            await engine.ExecuteAsync(parentId, CancellationToken.None).ConfigureAwait(false);
            return 0;
        }

        IWorkflowStore engineStore = mode is "child-created" or "parent-claim" or "child-manifest" or "wait-claim"
            ? CreateHardExitStore(options)
            : store;
        engine = new WorkflowEngine(
            engineStore,
            GroupChildWorkflow.Registry(_ =>
            {
                File.AppendAllText(receiptPath + ".callback", "child\n");
                if (mode == "child-running") Environment.Exit(HardExitCode);
            }),
            EngineOptions());
        if (mode == "start-completed")
        {
            await engine.StartAsync(
                GroupChildWorkflow.ParentName,
                "1",
                "probe",
                workflowRunId: parentId,
                cancellationToken: CancellationToken.None).ConfigureAwait(false);
            File.WriteAllText(receiptPath, JsonSerializer.Serialize(new { ParentId = parentId, ChildId = childId }));
            await engine.ExecuteAsync(parentId, CancellationToken.None).ConfigureAwait(false);
            return 0;
        }
        File.WriteAllText(receiptPath, JsonSerializer.Serialize(new { ParentId = parentId, ChildId = childId, Mode = mode }));
        await engine.ExecuteAsync(parentId, CancellationToken.None).ConfigureAwait(false);
        return 0;
    }

    private static async Task<DateTimeOffset> RecoverLeasesAsync(SqliteWorkflowStore store, Guid parentId, Guid childId, bool skipPeerChild)
    {
        var parent = await store.GetRunAsync(parentId).ConfigureAwait(false);
        var child = await store.GetRunAsync(childId).ConfigureAwait(false);
        var recoveryAt = DateTimeOffset.UtcNow;
        if (parent?.LeaseExpiresAt is { } parentExpiry && parentExpiry > recoveryAt)
            recoveryAt = parentExpiry;
        foreach (var step in await store.GetStepsAsync(parentId).ConfigureAwait(false))
            if (step.LeaseExpiresAt is { } expiry && expiry > recoveryAt) recoveryAt = expiry;
        if (!skipPeerChild && child?.LeaseExpiresAt is { } childExpiry &&
            child.Status == WorkflowStatus.Running && child.LeaseOwner is not null)
            recoveryAt = childExpiry > recoveryAt ? childExpiry : recoveryAt;
        if (!skipPeerChild && child is not null)
            foreach (var step in await store.GetStepsAsync(childId).ConfigureAwait(false))
                if (step.LeaseExpiresAt is { } expiry && expiry > recoveryAt) recoveryAt = expiry;
        recoveryAt = recoveryAt.AddMilliseconds(10);
        await store.RecoverExpiredLeasesAsync(recoveryAt, CancellationToken.None).ConfigureAwait(false);
        return recoveryAt;
    }

    private static void BlockAsPeer(string receiptPath)
    {
        File.AppendAllText(receiptPath + ".callback", "child\n");
        File.WriteAllText(receiptPath + ".started", "started");
        var release = receiptPath + ".release";
        while (!File.Exists(release)) Thread.Sleep(20);
    }

    private static Guid HashId(string value) => new(SHA256.HashData(Encoding.UTF8.GetBytes(value)).AsSpan(0, 16));
    private static ZhinuOptions EngineOptions() => new()
    {
        PollInterval = TimeSpan.FromMilliseconds(10),
        LeaseDuration = TimeSpan.FromSeconds(3),
        LeaseRenewalInterval = TimeSpan.FromMilliseconds(800)
    };

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static SqliteWorkflowStore CreateHardExitStore(ZhinuSqliteOptions options) =>
        new(new HardExitSqliteDatabase(new SqliteDatabase(options)));

    private static async Task InstallTriggerAsync(
        IZhinuSqliteDatabase database,
        string name,
        string sql)
    {
        await using var connection = await database.OpenAsync().ConfigureAwait(false);
        connection.CreateFunction("zhinu_hard_exit", () =>
        {
            Environment.Exit(HardExitCode);
            return 0;
        });
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    public interface IGroupChildProbeStore : IWorkflowStore, IWorkflowGroupRepository, IWorkflowWaitRepository { }

    public class GroupChildStoreProxy : DispatchProxy
    {
        private IWorkflowStore? workflow;
        private IWorkflowGroupRepository? groups;
        private IWorkflowWaitRepository? waits;

        public Guid? ExitAfterCompleteRunId { get; set; }
        public Guid? ExitAfterParkChildId { get; set; }
        public string? ExitAfterStepOutput { get; set; }

        public void Initialize(IWorkflowStore workflowStore, IWorkflowGroupRepository groupStore, IWorkflowWaitRepository waitStore)
        {
            workflow = workflowStore;
            groups = groupStore;
            waits = waitStore;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            args ??= [];
            object? target = targetMethod.DeclaringType == typeof(IWorkflowGroupRepository)
                ? groups
                : targetMethod.DeclaringType == typeof(IWorkflowWaitRepository)
                    ? waits
                    : workflow;
            if (target is null)
                throw new InvalidOperationException("Child process probe store proxy is uninitialized.");
            object? result;
            try
            {
                result = targetMethod.Invoke(target, args);
            }
            catch (TargetInvocationException exception) when (exception.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
                throw;
            }

            if (targetMethod.Name == nameof(IWorkflowRepository.CompleteRunAsync) &&
                args[0] is Guid runId && runId == ExitAfterCompleteRunId)
                return ExitAfterValueTaskAsync((ValueTask)result!);
            if (targetMethod.Name == nameof(IWorkflowWaitRepository.ParkWaitAsync) &&
                args[0] is ParkWaitRequest request && request.ChildRunId == ExitAfterParkChildId)
                return ExitAfterParkAsync((ValueTask<WorkflowWait>)result!);
            if (targetMethod.Name == nameof(IWorkflowStepRepository.CompleteStepWithEventsAsync) &&
                ExitAfterStepOutput is { } marker && args.Any(item => item is string text && text.Contains(marker, StringComparison.Ordinal)))
                return ExitAfterStepAsync((ValueTask<IReadOnlyList<WorkflowEvent>>)result!);
            return result;
        }

        private static async ValueTask ExitAfterValueTaskAsync(ValueTask operation)
        {
            await operation.ConfigureAwait(false);
            Environment.Exit(HardExitCode);
        }

        private static async ValueTask<IReadOnlyList<WorkflowEvent>> ExitAfterStepAsync(
            ValueTask<IReadOnlyList<WorkflowEvent>> operation)
        {
            var events = await operation.ConfigureAwait(false);
            Environment.Exit(HardExitCode);
            return events;
        }

        private static async ValueTask<WorkflowWait> ExitAfterParkAsync(ValueTask<WorkflowWait> operation)
        {
            _ = await operation.ConfigureAwait(false);
            Environment.Exit(HardExitCode);
            throw new InvalidOperationException("Process termination unexpectedly returned.");
        }
    }

    private sealed class HardExitSqliteDatabase(IZhinuSqliteDatabase inner) : IZhinuSqliteDatabase
    {
        public ZhinuSqliteOptions Options => inner.Options;
        public TimeProvider TimeProvider => inner.TimeProvider;
        public ValueTask InitializeAsync(CancellationToken cancellationToken = default) => inner.InitializeAsync(cancellationToken);
        public ValueTask EnsureInitializedAsync(CancellationToken cancellationToken = default) => inner.EnsureInitializedAsync(cancellationToken);
        public ValueTask CheckpointAsync(CancellationToken cancellationToken = default) => inner.CheckpointAsync(cancellationToken);
        public ValueTask VacuumAsync(CancellationToken cancellationToken = default) => inner.VacuumAsync(cancellationToken);
        public async ValueTask<SqliteConnection> OpenAsync(CancellationToken cancellationToken = default)
        {
            var connection = await inner.OpenAsync(cancellationToken).ConfigureAwait(false);
            connection.CreateFunction("zhinu_hard_exit", () =>
            {
                Environment.Exit(HardExitCode);
                return 0;
            });
            return connection;
        }
    }
}
