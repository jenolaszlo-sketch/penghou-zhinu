using FluentAssertions;
using Microsoft.Data.Sqlite;
#pragma warning disable xUnit1051

namespace Penghou.Zhinu.Sqlite.Tests;

public sealed class WorkflowPersistenceExceptionTests : WorkflowEngineTestBase
{
    [Fact]
    public async Task StoreOperation_SurfacesWorkflowPersistenceException_NotRawSqlite()
    {
        var store = CreateStore();
        await store.InitializeAsync(TestContext.Current.CancellationToken);
        // Corrupt the database by dropping a table the store depends on.
        var db = new SqliteDatabase(new ZhinuSqliteOptions
        {
            DatabasePath = Path.Combine(root, "zhinu.db"),
            Pooling = false
        });
        await using (var connection = await db.OpenAsync(TestContext.Current.CancellationToken))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE workflow_runs;";
            await command.ExecuteNonQueryAsync();
        }

        var act = async () => await store.GetRunAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);
        var exception = await act.Should().ThrowAsync<WorkflowPersistenceException>();
        exception.And.InnerException.Should().BeOfType<SqliteException>();
    }
}
