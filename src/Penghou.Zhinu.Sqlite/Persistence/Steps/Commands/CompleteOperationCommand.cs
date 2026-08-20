using Microsoft.Data.Sqlite;

namespace Penghou.Zhinu.Sqlite.Persistence.Steps;

internal sealed class CompleteOperationCommand
{
    public async ValueTask ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid operationId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = SqliteStoreSupport.CreateCommand(connection, transaction, """
            UPDATE workflow_run_operations
            SET status = $completed, updated_at = $now, completed_at = $now
            WHERE operation_id = $operationId;
            """);
        command.Parameters.AddWithValue(
            "$completed",
            (int)WorkflowOperationStatus.Completed);
        command.Parameters.AddWithValue("$now", SqliteStoreSupport.FormatTimestamp(now));
        command.Parameters.AddWithValue(
            "$operationId",
            SqliteStoreSupport.Format(operationId));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
