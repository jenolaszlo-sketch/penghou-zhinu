using FluentAssertions;
using Microsoft.Data.Sqlite;
#pragma warning disable xUnit1051

namespace Penghou.Zhinu.Sqlite.Tests;

public sealed class QueryPlanTests : WorkflowEngineTestBase
{
    private async Task<(SqliteConnection Connection, Guid RunId)> CreatePopulatedDatabaseAsync(int artifacts = 500)
    {
        var db = new SqliteDatabase(new ZhinuSqliteOptions
        {
            DatabasePath = Path.Combine(root, "queryplan.db"),
            Pooling = false
        });
        await db.InitializeAsync(TestContext.Current.CancellationToken);
        var connection = await db.OpenAsync(TestContext.Current.CancellationToken);
        var runId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = $"INSERT INTO workflow_runs (id, workflow_name, workflow_version, status, created_at, updated_at, lease_generation) VALUES ('{runId}', 'w', '1', 0, '{now}', '{now}', 1);";
            await cmd.ExecuteNonQueryAsync();
        }
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = $"""
                WITH RECURSIVE c(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM c WHERE x < {artifacts})
                INSERT INTO workflow_artifacts (id, workflow_run_id, name, revision, artifact_type, location, created_at)
                SELECT lower(hex(randomblob(16))), '{runId}', 'a' || x, 1, 't', 'loc', '{now}' FROM c;
                """;
            await cmd.ExecuteNonQueryAsync();
        }
        return (connection, runId);
    }

    [Fact]
    public async Task Indexes_ExistForHotQueries()
    {
        await using var connection = (await CreatePopulatedDatabaseAsync()).Connection;
        string[] expected = [
            "ix_workflow_runs_runnable",
            "ix_workflow_runs_name_version",
            "ix_workflow_runs_created",
            "ix_workflow_runs_parent",
            "ix_workflow_steps_current",
            "ix_workflow_steps_run",
            "ix_workflow_steps_runnable",
            "ix_workflow_events_run_sequence",
            "ix_workflow_artifacts_created",
            "ix_workflow_artifacts_run",
            "ix_workflow_signals_run_name",
            "ix_workflow_step_dependencies_key",
            "ix_workflow_step_dependencies_depends_on",
            "ix_workflow_step_compensations_run"
        ];
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'index' AND name NOT LIKE 'sqlite_%';";
        var actual = new HashSet<string>();
        await using (var reader = await cmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
                actual.Add(reader.GetString(0));
        }
        expected.Should().OnlyContain(name => actual.Contains(name));
    }

    [Fact]
    public async Task ArtifactOrderingQuery_DoesNotTempSort()
    {
        var (connection, runId) = await CreatePopulatedDatabaseAsync(artifacts: 500);
        var sql = $"SELECT id FROM workflow_artifacts WHERE workflow_run_id = '{runId}' ORDER BY created_at, name, revision LIMIT 100;";
        var plans = await ExplainAsync(connection, sql);
        plans.Should().NotContain(plan => plan.Contains("TEMP B-TREE"));
    }

    [Fact]
    public async Task SignalDeliveryLookup_DoesNotTempSort()
    {
        var (connection, runId) = await CreatePopulatedDatabaseAsync();
        var sql = $"SELECT id FROM workflow_signals WHERE workflow_run_id = '{runId}' AND signal_name = 'sig' AND delivered_step_id IS NULL ORDER BY created_at LIMIT 1;";
        var plans = await ExplainAsync(connection, sql);
        plans.Should().NotContain(plan => plan.Contains("TEMP B-TREE"));
    }

    [Fact]
    public async Task Scale_ManyRunsAndEvents_QueriesCompleteCorrectly()
    {
        var db = new SqliteDatabase(new ZhinuSqliteOptions
        {
            DatabasePath = Path.Combine(root, "scale.db"),
            Pooling = false
        });
        await db.InitializeAsync(TestContext.Current.CancellationToken);
        await using var connection = await db.OpenAsync(TestContext.Current.CancellationToken);
        var now = DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        var runId = Guid.NewGuid();
        // 5,000 runs + 50,000 events for one run.
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = $"""
                WITH RECURSIVE c(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM c WHERE x < 5000)
                INSERT INTO workflow_runs (id, workflow_name, workflow_version, status, created_at, updated_at, lease_generation)
                SELECT lower(hex(randomblob(16))), 'scale', '1', 0, '{now}', '{now}', 1 FROM c;
                """;
            await cmd.ExecuteNonQueryAsync();
        }
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = $"""
                INSERT INTO workflow_runs (id, workflow_name, workflow_version, status, created_at, updated_at, lease_generation)
                VALUES ('{runId}', 'scale', '1', 0, '{now}', '{now}', 1);
                WITH RECURSIVE c(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM c WHERE x < 50000)
                INSERT INTO workflow_events (workflow_run_id, event_type, timestamp)
                SELECT '{runId}', 'e', '{now}' FROM c;
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        var store = new SqliteWorkflowStore(db);
        var query = await store.GetRunsAsync(new RunQuery { WorkflowName = "scale", Limit = 100 }, TestContext.Current.CancellationToken);
        query.Should().HaveCount(100);
        var events = await store.GetEventsAsync(runId, 0, 1000, TestContext.Current.CancellationToken);
        events.Should().HaveCount(1000);
        events.Select(e => e.Sequence).Should().BeInAscendingOrder();
    }

    private static async Task<List<string>> ExplainAsync(SqliteConnection connection, string sql)
    {
        var plans = new List<string>();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "EXPLAIN QUERY PLAN " + sql;
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            plans.Add(reader.GetString(3));
        return plans;
    }
}
