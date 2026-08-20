using FluentAssertions;
using Microsoft.Data.Sqlite;

namespace Penghou.Zhinu.Sqlite.Tests;

public sealed class SchemaCompatibilityTests : WorkflowEngineTestBase
{
    [Fact]
    public async Task InitializeAsync_NewDatabase_RecordsCurrentSchemaVersion()
    {
        var store = CreateStore();
        await store.InitializeAsync(TestContext.Current.CancellationToken);

        await using var connection = new SqliteConnection(
            $"Data Source={Path.Combine(root, "zhinu.db")}");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT version FROM zhinu_schema WHERE id = 1;";
        var version = Convert.ToInt32(
            await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));

        version.Should().Be(ZhinuSqliteSchema.CurrentVersion);
    }

    [Fact]
    public async Task InitializeAsync_UnversionedDatabase_ExplainsRequiredReset()
    {
        Directory.CreateDirectory(root);
        await ExecuteAsync("CREATE TABLE workflow_runs (id TEXT PRIMARY KEY);");
        var store = CreateStore();

        var action = () => store.InitializeAsync(TestContext.Current.CancellationToken).AsTask();
        var exception = await action.Should()
            .ThrowAsync<ZhinuSchemaCompatibilityException>();

        exception.Which.ExpectedVersion.Should().Be(ZhinuSqliteSchema.CurrentVersion);
        exception.Which.DatabaseVersion.Should().BeNull();
        exception.Which.Message.Should().Contain("Recreate the preview database");
    }

    [Fact]
    public async Task InitializeAsync_IncompatibleVersion_ReportsBothVersions()
    {
        Directory.CreateDirectory(root);
        await ExecuteAsync("""
            CREATE TABLE zhinu_schema
            (
                id INTEGER PRIMARY KEY,
                version INTEGER NOT NULL
            );
            INSERT INTO zhinu_schema (id, version) VALUES (1, 999);
            """);
        var store = CreateStore();

        var action = () => store.InitializeAsync(TestContext.Current.CancellationToken).AsTask();
        var exception = await action.Should()
            .ThrowAsync<ZhinuSchemaCompatibilityException>();

        exception.Which.ExpectedVersion.Should().Be(ZhinuSqliteSchema.CurrentVersion);
        exception.Which.DatabaseVersion.Should().Be(999);
        exception.Which.Message.Should().Contain("schema 999")
            .And.Contain($"schema {ZhinuSqliteSchema.CurrentVersion}");
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new SqliteConnection(
            $"Data Source={Path.Combine(root, "zhinu.db")}");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }
}
