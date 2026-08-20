using Microsoft.Data.Sqlite;

namespace Penghou.Zhinu.Sqlite.Persistence.Steps;

internal sealed class GetActiveOperationQuery
{
    public async ValueTask<WorkflowRunOperation?> ExecuteAsync(
        SqliteConnection connection,
        Guid workflowRunId,
        CancellationToken cancellationToken)
    {
        await using var command = SqliteStoreSupport.CreateCommand(connection, null, $"""
            SELECT {SqliteStoreSupport.OperationColumns}
            FROM workflow_run_operations
            WHERE workflow_run_id = $runId
              AND status NOT IN ($completed, $failed)
            ORDER BY created_at, operation_id
            LIMIT 1;
            """);
        command.Parameters.AddWithValue("$runId", SqliteStoreSupport.Format(workflowRunId));
        command.Parameters.AddWithValue(
            "$completed",
            (int)WorkflowOperationStatus.Completed);
        command.Parameters.AddWithValue(
            "$failed",
            (int)WorkflowOperationStatus.Failed);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? SqliteStoreSupport.ReadOperation(reader)
            : null;
    }
}
