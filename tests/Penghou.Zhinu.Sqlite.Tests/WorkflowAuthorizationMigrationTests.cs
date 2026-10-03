using FluentAssertions;
using Microsoft.Data.Sqlite;

namespace Penghou.Zhinu.Sqlite.Tests;

public sealed class WorkflowAuthorizationMigrationTests
{
    [Fact]
    public async Task InitializeAsync_FailedVersionBump_RollsBackAuthorizationSchemaAndColumns()
    {
        var directory = Path.Combine(Path.GetTempPath(), "penghou-zhinu-migration", Guid.NewGuid().ToString("D"));
        Directory.CreateDirectory(directory);
        var databasePath = Path.Combine(directory, "zhinu.db");
        try
        {
            var schemaPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "zhinu-schema-v5.sql");
            var legacySchema = await File.ReadAllTextAsync(schemaPath, TestContext.Current.CancellationToken);
            await using (var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False"))
            {
                await connection.OpenAsync(TestContext.Current.CancellationToken);
                await using var schema = connection.CreateCommand();
                schema.CommandText = legacySchema + """
                    CREATE TRIGGER reject_v6 BEFORE UPDATE OF version ON zhinu_schema
                    WHEN NEW.version = 6 BEGIN SELECT RAISE(ABORT, 'injected migration failure'); END;
                    """;
                await schema.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }

            var initialize = () => CreateStore(databasePath).InitializeAsync(TestContext.Current.CancellationToken).AsTask();
            (await initialize.Should().ThrowAsync<WorkflowPersistenceException>())
                .Which.InnerException.Should().BeOfType<SqliteException>();

            await using var verify = new SqliteConnection($"Data Source={databasePath};Pooling=False");
            await verify.OpenAsync(TestContext.Current.CancellationToken);
            await using var check = verify.CreateCommand();
            check.CommandText = """
                SELECT version,
                       (SELECT COUNT(*) FROM pragma_table_info('workflow_runs') WHERE name='authorization_provider_id'),
                       (SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='workflow_authorization_checkpoints')
                FROM zhinu_schema WHERE id=1;
                """;
            await using var reader = await check.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            (await reader.ReadAsync(TestContext.Current.CancellationToken)).Should().BeTrue();
            reader.GetInt32(0).Should().Be(5);
            reader.GetInt32(1).Should().Be(0);
            reader.GetInt32(2).Should().Be(0);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task InitializeAsync_V5Database_PreservesLegacyRowsAndAddsNullableAuthorizationStorage()
    {
        var directory = Path.Combine(Path.GetTempPath(), "penghou-zhinu-migration", Guid.NewGuid().ToString("D"));
        Directory.CreateDirectory(directory);
        var databasePath = Path.Combine(directory, "zhinu.db");
        try
        {
            var runId = Guid.NewGuid().ToString("D");
            var stepId = Guid.NewGuid().ToString("D");
            var compensationId = Guid.NewGuid().ToString("D");
            var schemaPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "zhinu-schema-v5.sql");
            var legacySchema = await File.ReadAllTextAsync(schemaPath, TestContext.Current.CancellationToken);
            await using (var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False"))
            {
                await connection.OpenAsync(TestContext.Current.CancellationToken);
                await using (var schema = connection.CreateCommand())
                {
                    schema.CommandText = legacySchema;
                    await schema.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
                }
                await using var seed = connection.CreateCommand();
                seed.CommandText = """
                    INSERT INTO workflow_runs(id,workflow_name,workflow_version,status,created_at,updated_at)
                    VALUES($run,'legacy','1',0,'2026-10-01T00:00:00.0000000+00:00','2026-10-01T00:00:00.0000000+00:00');
                    INSERT INTO workflow_steps(id,workflow_run_id,step_key,status,attempt,created_at)
                    VALUES($step,$run,'legacy-step',2,3,'2026-10-01T00:00:00.0000000+00:00');
                    INSERT INTO workflow_step_compensations(id,workflow_run_id,step_key,revision,compensation_name,status,attempt,created_at)
                    VALUES($comp,$run,'legacy-step',1,'legacy-compensation',0,2,'2026-10-01T00:00:00.0000000+00:00');
                    """;
                seed.Parameters.AddWithValue("$run", runId);
                seed.Parameters.AddWithValue("$step", stepId);
                seed.Parameters.AddWithValue("$comp", compensationId);
                await seed.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }

            await CreateStore(databasePath).InitializeAsync(TestContext.Current.CancellationToken);
            await CreateStore(databasePath).InitializeAsync(TestContext.Current.CancellationToken);

            await using var verify = new SqliteConnection($"Data Source={databasePath};Pooling=False");
            await verify.OpenAsync(TestContext.Current.CancellationToken);
            await using var check = verify.CreateCommand();
            check.CommandText = """
                SELECT version,
                       (SELECT COUNT(*) FROM workflow_runs WHERE id=$run AND authorization_provider_id IS NULL AND authorization_binding_id IS NULL),
                       (SELECT COUNT(*) FROM workflow_steps WHERE id=$step AND authorization_declaration_hash IS NULL AND attempt=3),
                       (SELECT COUNT(*) FROM workflow_step_compensations WHERE id=$comp AND authorization_declaration_json IS NULL AND attempt=2)
                FROM zhinu_schema WHERE id=1;
                """;
            check.Parameters.AddWithValue("$run", runId);
            check.Parameters.AddWithValue("$step", stepId);
            check.Parameters.AddWithValue("$comp", compensationId);
            await using var reader = await check.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            (await reader.ReadAsync(TestContext.Current.CancellationToken)).Should().BeTrue();
            reader.GetInt32(0).Should().Be(ZhinuSqliteSchema.CurrentVersion);
            reader.GetInt32(1).Should().Be(1);
            reader.GetInt32(2).Should().Be(1);
            reader.GetInt32(3).Should().Be(1);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static SqliteWorkflowStore CreateStore(string databasePath) => new(new ZhinuSqliteOptions
    {
        DatabasePath = databasePath,
        BusyTimeout = TimeSpan.FromSeconds(2),
        Pooling = false
    });
}
