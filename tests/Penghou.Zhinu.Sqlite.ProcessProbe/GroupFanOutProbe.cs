using Microsoft.Data.Sqlite;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;

namespace Penghou.Zhinu.Sqlite.ProcessProbe;

internal static class GroupFanOutProbe
{
    public static async Task<int> RunAsync(string databasePath, string runIdPath, string mode)
    {
        if (mode is not ("admit" or "partial" or "close" or "closed"))
            throw new ArgumentException($"Unknown grouped fan-out process mode '{mode}'.", nameof(mode));

        var options = new ZhinuSqliteOptions
        {
            DatabasePath = databasePath,
            BusyTimeout = TimeSpan.FromSeconds(5),
            Pooling = false
        };
        var database = new SqliteDatabase(options);
        await database.InitializeAsync().ConfigureAwait(false);
        if (mode != "closed")
        {
            var (trigger, table, condition) = mode switch
            {
                "admit" => ("zhinu_hard_exit_fanout_admit", "workflow_group_fanout_items", " WHEN NEW.item_key='b'"),
                "partial" => ("zhinu_hard_exit_fanout_partial", "workflow_step_group_memberships", " WHEN NEW.step_key='items.b'"),
                "close" => ("zhinu_hard_exit_fanout_close", "workflow_group_fanout_closures", string.Empty),
                _ => throw new ArgumentOutOfRangeException(nameof(mode))
            };
            await InstallExitTriggerAsync(database, trigger, table, condition).ConfigureAwait(false);
        }

        var store = new SqliteWorkflowStore(new HardExitSqliteDatabase(database));
        var workflowName = $"process-group-fanout-{mode}";
        var manifest = CreateManifest();
        var engine = new WorkflowEngine(
            store,
            new WorkflowRegistry().Register(
                new WorkflowRegistration<string, string>(
                    new WorkflowDefinition { Name = workflowName, Version = "1" },
                    () => new GroupFanOutWorkflow(mode),
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
            "a,b",
            cancellationToken: CancellationToken.None).ConfigureAwait(false);
        File.WriteAllText(runIdPath, runId.ToString("D"));
        await engine.ExecuteAsync(runId, CancellationToken.None).ConfigureAwait(false);
        return 0;
    }

    private static async Task InstallExitTriggerAsync(
        IZhinuSqliteDatabase database,
        string triggerName,
        string tableName,
        string condition)
    {
        await using var connection = await database.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            CREATE TRIGGER {triggerName}
            BEFORE INSERT ON {tableName}{condition}
            BEGIN
                SELECT zhinu_hard_exit();
            END;
            """;
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static ActivityGroupManifest CreateManifest() => ActivityGroupManifest.Create(new[]
    {
        new ActivityGroupDefinition("root", ActivityGroupKind.Logical, "Root", childOrdinal: 0),
        new ActivityGroupDefinition("work", ActivityGroupKind.Logical, "Work", "root")
    });

    private sealed class GroupFanOutWorkflow(string mode) : IWorkflow<string, string>
    {
        public async Task<string> RunAsync(
            WorkflowContext context,
            string input,
            CancellationToken cancellationToken)
        {
            var selected = await context.StepAsync(
                "select",
                input,
                (value, _, _) => Task.FromResult(value.Split(',', StringSplitOptions.RemoveEmptyEntries)),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            var parent = context.GetActivityGroup("root");
            var options = new StepOptions
            {
                DependsOn = ["select"],
                FanOutGroup = new ActivityGroupFanOutBinding(
                    new ActivityGroupMembership("work"),
                    "select",
                    parent)
            };
            var result = await context.FanOutAsync(
                "items",
                selected,
                item => item,
                (item, _, _) => Task.FromResult(item.ToUpperInvariant()),
                new FanOutOptions { MaxDegreeOfParallelism = 1 },
                options,
                cancellationToken).ConfigureAwait(false);
            if (mode is "closed")
                Environment.Exit(93);
            return string.Join('|', result);
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
}
