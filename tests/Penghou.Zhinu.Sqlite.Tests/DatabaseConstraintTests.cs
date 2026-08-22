using FluentAssertions;
using Microsoft.Data.Sqlite;
using System.Globalization;
#pragma warning disable xUnit1051

namespace Penghou.Zhinu.Sqlite.Tests;

public sealed class DatabaseConstraintTests : WorkflowEngineTestBase
{
    private async Task<IZhinuSqliteDatabase> CreateFreshDatabaseAsync(string name)
    {
        var database = new SqliteDatabase(new ZhinuSqliteOptions
        {
            DatabasePath = Path.Combine(root, $"{name}.db"),
            Pooling = false
        });
        await database.InitializeAsync(TestContext.Current.CancellationToken);
        return database;
    }

    private static async Task<object?> ScalarAsync(IZhinuSqliteDatabase db, string sql)
    {
        await using var connection = await db.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
    }

    private static async Task ExecuteAsync(IZhinuSqliteDatabase db, string sql)
    {
        await using var connection = await db.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Schema_CurrentVersion_IsPersisted()
    {
        var db = await CreateFreshDatabaseAsync("version");
        var version = Convert.ToInt32(
            await ScalarAsync(db, "SELECT version FROM zhinu_schema WHERE id = 1;"));
        version.Should().Be(ZhinuSqliteSchema.CurrentVersion);
    }

    [Fact]
    public async Task SelfDependencyEdge_IsRejectedByDatabase()
    {
        var db = await CreateFreshDatabaseAsync("selfdep");
        var runId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await ExecuteAsync(db, $"""
            INSERT INTO workflow_runs (id, workflow_name, workflow_version, status,
                created_at, updated_at, lease_generation)
            VALUES ('{runId}', 'constraint', '1', 0, '{Format(now)}', '{Format(now)}', 1);
            """);

        var act = () => ExecuteAsync(db, $"""
            INSERT INTO workflow_step_dependencies (run_id, step_key, depends_on_step_key, created_at)
            VALUES ('{runId}', 'a', 'a', '{Format(now)}');
            """);
        await act.Should().ThrowAsync<SqliteException>()
            .WithMessage("*CHECK*");
    }

    [Fact]
    public async Task InvalidStepStatus_IsRejectedByDatabase()
    {
        var db = await CreateFreshDatabaseAsync("badstatus");
        var runId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await ExecuteAsync(db, $"""
            INSERT INTO workflow_runs (id, workflow_name, workflow_version, status,
                created_at, updated_at, lease_generation)
            VALUES ('{runId}', 'constraint-status', '1', 0, '{Format(now)}', '{Format(now)}', 1);
            """);

        var act = () => ExecuteAsync(db, $"""
            INSERT INTO workflow_steps
                (id, workflow_run_id, step_key, status, attempt, created_at,
                 input_type, output_type, revision, lease_generation)
            VALUES
                ('{Guid.NewGuid()}', '{runId}', 'bad', 99, 0, '{Format(now)}',
                 't', 't', 1, 1);
            """);
        await act.Should().ThrowAsync<SqliteException>()
            .WithMessage("*CHECK*");
    }

    [Fact]
    public async Task NegativeAttempt_IsRejectedByDatabase()
    {
        var db = await CreateFreshDatabaseAsync("negattempt");
        var runId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await ExecuteAsync(db, $"""
            INSERT INTO workflow_runs (id, workflow_name, workflow_version, status,
                created_at, updated_at, lease_generation)
            VALUES ('{runId}', 'constraint-attempt', '1', 0, '{Format(now)}', '{Format(now)}', 1);
            """);

        var act = () => ExecuteAsync(db, $"""
            INSERT INTO workflow_steps
                (id, workflow_run_id, step_key, status, attempt, created_at,
                 input_type, output_type, revision, lease_generation)
            VALUES
                ('{Guid.NewGuid()}', '{runId}', 'bad', 1, -1, '{Format(now)}',
                 't', 't', 1, 1);
            """);
        await act.Should().ThrowAsync<SqliteException>()
            .WithMessage("*CHECK*");
    }

    private static string Format(DateTimeOffset value) =>
        value.ToString("O", CultureInfo.InvariantCulture);
}
