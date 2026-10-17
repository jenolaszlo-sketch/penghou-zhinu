using System.Globalization;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Microsoft.Data.Sqlite;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;
using Penghou.Zhinu.Sqlite.Tests;

namespace Penghou.Zhinu.Sqlite.ProcessProbe;

public static partial class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 4 && args[0] == "--zhinu-trace-probe")
            return await TraceProbeAsync(args[1], args[2], args[3]).ConfigureAwait(false);
        if (args.Length == 4 && args[0] == "--zhinu-loop-probe")
            return await LoopProbeAsync(args).ConfigureAwait(false);
        if (args.Length == 4 && args[0] == "--zhinu-group-probe")
            return await GroupProbeAsync(args).ConfigureAwait(false);
        if (args.Length == 5 && args[0] == "--zhinu-group-atomic-probe")
            return await GroupAtomicProbeAsync(args).ConfigureAwait(false);
        if (args.Length == 4 && args[0] == "--zhinu-group-fanout-probe")
            return await GroupFanOutProbe.RunAsync(args[1], args[2], args[3]).ConfigureAwait(false);
        if (args.Length == 5 && args[0] == "--zhinu-group-fork-probe")
            return await GroupForkProbeAsync(args[1], Guid.Parse(args[2]), Guid.Parse(args[3]), args[4]).ConfigureAwait(false);
        return 2;
    }

    // Reads a run's durable trace id from a freshly opened store in a separate
    // process, with no inherited Activity context, and writes it to a file.
    // This proves trace correlation lives in durable storage rather than in
    // process-local state.
    private static async Task<int> TraceProbeAsync(string databasePath, string runId, string outputPath)
    {
        var store = new SqliteWorkflowStore(new ZhinuSqliteOptions
        {
            DatabasePath = databasePath,
            BusyTimeout = TimeSpan.FromSeconds(5),
            Pooling = false
        });
        var run = await store.GetRunAsync(Guid.Parse(runId), CancellationToken.None).ConfigureAwait(false);
        File.WriteAllText(outputPath, run?.TraceId ?? "(no-trace)");
        return run is null ? 3 : 0;
    }

    private static async Task<int> LoopProbeAsync(string[] args)
    {
        var databasePath = args[1];
        var runIdPath = args[2];
        var mode = args[3];
        var faultPoint = mode switch
        {
            "before" => FaultInjectingWorkflowStore.BeforeStepCompletionCommit,
            "after" => FaultInjectingWorkflowStore.AfterStepCompletionCommit,
            _ => throw new ArgumentException($"Unknown probe mode '{mode}'.", nameof(args))
        };
        var store = new FaultInjectingWorkflowStore(
            new SqliteWorkflowStore(
                new ZhinuSqliteOptions
                {
                    DatabasePath = databasePath,
                    BusyTimeout = TimeSpan.FromSeconds(5),
                    Pooling = false
                }));
        store.ArmProcessTerminationAtOutput(faultPoint, "\"Kind\"");
        // The process probe intentionally arms the decorator before any
        // execution; every step completion must pass through it.
        const string workflowName = "process-loop-boundary";
        var engine = new WorkflowEngine(
            store,
            new WorkflowRegistry().Register(
                workflowName,
                "1",
                new ProbeLoopWorkflow()),
            new ZhinuOptions
            {
                PollInterval = TimeSpan.FromMilliseconds(10),
                LeaseDuration = TimeSpan.FromSeconds(2),
                LeaseRenewalInterval = TimeSpan.FromMilliseconds(600)
            });
        var runId = await engine.StartAsync(
            workflowName,
            "1",
            "probe",
            cancellationToken: CancellationToken.None).ConfigureAwait(false);
        File.WriteAllText(runIdPath, runId.ToString("D"));
        await engine.ExecuteAsync(runId, CancellationToken.None).ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> GroupProbeAsync(string[] args)
    {
        var databasePath = args[1];
        var runIdPath = args[2];
        var mode = args[3];
        var faultPoint = mode switch
        {
            "before" => FaultInjectingWorkflowStore.BeforeStepCompletionCommit,
            "after" => FaultInjectingWorkflowStore.AfterStepCompletionCommit,
            _ => throw new ArgumentException($"Unknown probe mode '{mode}'.", nameof(args))
        };
        var sqlite = new SqliteWorkflowStore(
            new ZhinuSqliteOptions
            {
                DatabasePath = databasePath,
                BusyTimeout = TimeSpan.FromSeconds(5),
                Pooling = false
            });
        var faultStore = new FaultInjectingWorkflowStore(sqlite);
        var store = DispatchProxy.Create<IProbeGroupStore, ProbeGroupStoreProxy>();
        ((ProbeGroupStoreProxy)(object)store).Initialize(faultStore, sqlite);
        // The first iteration commits normally. Kill at the second loop commit,
        // after both iteration-scoped memberships have been claimed.
        faultStore.ArmProcessTerminationAtOutput(faultPoint, "\"State\":2");

        const string workflowName = "process-group-boundary";
        var manifest = ActivityGroupManifest.Create(new[]
        {
            new ActivityGroupDefinition("root", ActivityGroupKind.Logical, "Root", childOrdinal: 0),
            new ActivityGroupDefinition("iteration", ActivityGroupKind.Logical, "Iteration", "root")
        });
        var engine = new WorkflowEngine(
            store,
            new WorkflowRegistry().Register(
                new WorkflowRegistration<string, string>(
                    new WorkflowDefinition { Name = workflowName, Version = "1" },
                    () => new ProbeGroupWorkflow(),
                    manifest)),
            new ZhinuOptions
            {
                PollInterval = TimeSpan.FromMilliseconds(10),
                LeaseDuration = TimeSpan.FromSeconds(2),
                LeaseRenewalInterval = TimeSpan.FromMilliseconds(600)
            });
        var runId = await engine.StartAsync(
            workflowName,
            "1",
            "probe",
            cancellationToken: CancellationToken.None).ConfigureAwait(false);
        File.WriteAllText(runIdPath, runId.ToString("D"));
        await engine.ExecuteAsync(runId, CancellationToken.None).ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> GroupAtomicProbeAsync(string[] args)
    {
        var databasePath = args[1];
        var runId = Guid.Parse(args[2]);
        var runIdPath = args[3];
        var mode = args[4];
        if (mode is not ("manifest-install" or "group-claim"))
            throw new ArgumentException($"Unknown atomic probe mode '{mode}'.", nameof(args));

        var options = new ZhinuSqliteOptions
        {
            DatabasePath = databasePath,
            BusyTimeout = TimeSpan.FromSeconds(5),
            Pooling = false
        };
        var database = new SqliteDatabase(options);
        await database.InitializeAsync().ConfigureAwait(false);
        if (mode == "manifest-install")
        {
            await InstallExitTriggerAsync(
                database,
                "zhinu_hard_exit_group_manifest",
                "workflow_group_manifests").ConfigureAwait(false);
        }

        var store = new SqliteWorkflowStore(new HardExitSqliteDatabase(database));
        var manifest = CreateGroupManifest();
        var engine = new WorkflowEngine(
            store,
            new WorkflowRegistry().Register(
                new WorkflowRegistration<string, string>(
                    new WorkflowDefinition { Name = "process-group-atomic", Version = "1" },
                    () => new ProbeAtomicGroupWorkflow(),
                    manifest)),
            new ZhinuOptions
            {
                PollInterval = TimeSpan.FromMilliseconds(10),
                LeaseDuration = TimeSpan.FromSeconds(2),
                LeaseRenewalInterval = TimeSpan.FromMilliseconds(600)
            });

        // Make the selected identity observable before admission can terminate
        // the process halfway through the grouped-run transaction.
        File.WriteAllText(runIdPath, runId.ToString("D"));
        await engine.StartAsync(
            "process-group-atomic",
            "1",
            "probe",
            workflowRunId: runId,
            cancellationToken: CancellationToken.None).ConfigureAwait(false);
        if (mode == "group-claim")
        {
            await InstallExitTriggerAsync(
                database,
                "zhinu_hard_exit_group_claim",
                "workflow_step_group_memberships").ConfigureAwait(false);
        }
        await engine.ExecuteAsync(runId, CancellationToken.None).ConfigureAwait(false);
        return 0;
    }

    private static async Task InstallExitTriggerAsync(
        IZhinuSqliteDatabase database,
        string triggerName,
        string tableName)
    {
        await using var connection = await database.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            CREATE TRIGGER {triggerName}
            BEFORE INSERT ON {tableName}
            BEGIN
                SELECT zhinu_hard_exit();
            END;
            """;
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static ActivityGroupManifest CreateGroupManifest() => ActivityGroupManifest.Create(new[]
    {
        new ActivityGroupDefinition("root", ActivityGroupKind.Logical, "Root", childOrdinal: 0),
        new ActivityGroupDefinition("iteration", ActivityGroupKind.Logical, "Iteration", "root")
    });

    private sealed class ProbeGroupWorkflow : IWorkflow<string, string>
    {
        public async Task<string> RunAsync(
            WorkflowContext context,
            string input,
            CancellationToken cancellationToken)
        {
            var root = context.GetActivityGroup("root");
            var result = await context.LoopAsync(
                "reviewCycle",
                0,
                state => state < 2,
                async (iteration, token) =>
                {
                    var occurrence = iteration.GetActivityGroup("iteration", root);
                    var next = await iteration.StepAsync(
                        "work",
                        iteration.State,
                        (state, _, _) => Task.FromResult(state + 1),
                        new StepOptions
                        {
                            Group = new ActivityGroupStepBinding(
                                new ActivityGroupMembership("iteration"),
                                occurrence)
                        },
                        token).ConfigureAwait(false);
                    return iteration.Continue(next);
                },
                new LoopOptions(maxIterations: 2),
                cancellationToken).ConfigureAwait(false);
            return result.ToString(CultureInfo.InvariantCulture);
        }
    }

    private sealed class ProbeAtomicGroupWorkflow : IWorkflow<string, string>
    {
        public async Task<string> RunAsync(
            WorkflowContext context,
            string input,
            CancellationToken cancellationToken)
        {
            var root = context.GetActivityGroup("root");
            var result = await context.LoopAsync(
                "reviewCycle",
                0,
                state => state < 1,
                async (iteration, token) =>
                {
                    var occurrence = iteration.GetActivityGroup("iteration", root);
                    var next = await iteration.StepAsync(
                        "work",
                        iteration.State,
                        (state, _, _) => Task.FromResult(state + 1),
                        new StepOptions
                        {
                            Group = new ActivityGroupStepBinding(
                                new ActivityGroupMembership("iteration"),
                                occurrence)
                        },
                        token).ConfigureAwait(false);
                    return iteration.Continue(next);
                },
                new LoopOptions(maxIterations: 1),
                cancellationToken).ConfigureAwait(false);
            return result.ToString(CultureInfo.InvariantCulture);
        }
    }

    public interface IProbeGroupStore : IWorkflowStore, IWorkflowGroupRepository { }

    public class ProbeGroupStoreProxy : DispatchProxy
    {
        private IWorkflowStore? workflowStore;
        private IWorkflowGroupRepository? groupStore;

        public void Initialize(IWorkflowStore workflowStore, IWorkflowGroupRepository groupStore)
        {
            this.workflowStore = workflowStore;
            this.groupStore = groupStore;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            object? target = targetMethod.DeclaringType == typeof(IWorkflowGroupRepository)
                ? groupStore
                : workflowStore;
            if (target is null)
                throw new InvalidOperationException("Probe store proxy was not initialized.");
            try
            {
                return targetMethod.Invoke(target, args);
            }
            catch (TargetInvocationException exception) when (exception.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
                throw;
            }
        }
    }

    private sealed class HardExitSqliteDatabase(IZhinuSqliteDatabase inner) : IZhinuSqliteDatabase
    {
        public ZhinuSqliteOptions Options => inner.Options;
        public TimeProvider TimeProvider => inner.TimeProvider;
        public ValueTask InitializeAsync(CancellationToken cancellationToken = default) =>
            inner.InitializeAsync(cancellationToken);
        public ValueTask EnsureInitializedAsync(CancellationToken cancellationToken = default) =>
            inner.EnsureInitializedAsync(cancellationToken);
        public ValueTask CheckpointAsync(CancellationToken cancellationToken = default) =>
            inner.CheckpointAsync(cancellationToken);
        public ValueTask VacuumAsync(CancellationToken cancellationToken = default) =>
            inner.VacuumAsync(cancellationToken);

        public async ValueTask<SqliteConnection> OpenAsync(CancellationToken cancellationToken = default)
        {
            var connection = await inner.OpenAsync(cancellationToken).ConfigureAwait(false);
            connection.CreateFunction("zhinu_hard_exit", () =>
            {
                Environment.Exit(93);
                return 0;
            });
            return connection;
        }
    }

    private sealed class ProbeLoopWorkflow : IWorkflow<string, string>
    {
        public async Task<string> RunAsync(
            WorkflowContext context,
            string input,
            CancellationToken cancellationToken)
        {
            var result = await context.LoopAsync(
                "state",
                0,
                state => state < 1,
                async (iteration, token) =>
                {
                    var next = await iteration.StepAsync(
                        "increment",
                        iteration.State,
                        (state, _, _) => Task.FromResult(state + 1),
                        cancellationToken: token).ConfigureAwait(false);
                    return iteration.Continue(next);
                },
                new LoopOptions(maxIterations: 2),
                cancellationToken).ConfigureAwait(false);
            return result.ToString(CultureInfo.InvariantCulture);
        }
    }
}
