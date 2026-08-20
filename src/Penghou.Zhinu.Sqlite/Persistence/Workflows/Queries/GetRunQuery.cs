using Microsoft.Data.Sqlite;

namespace Penghou.Zhinu.Sqlite.Persistence.Workflows;

internal sealed class GetRunQuery
{
    public async ValueTask<WorkflowRun?> ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid workflowRunId,
        CancellationToken cancellationToken)
    {
        await using var command = SqliteStoreSupport.CreateCommand(connection, transaction, $"""
            SELECT {SqliteStoreSupport.RunColumns}
            FROM workflow_runs
            WHERE id = $id;
            """);
        command.Parameters.AddWithValue("$id", SqliteStoreSupport.Format(workflowRunId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? SqliteStoreSupport.ReadRun(reader)
            : null;
    }
}
