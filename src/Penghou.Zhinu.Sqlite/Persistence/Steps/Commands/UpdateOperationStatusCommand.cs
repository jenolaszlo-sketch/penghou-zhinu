using Microsoft.Data.Sqlite;

namespace Penghou.Zhinu.Sqlite.Persistence.Steps;

internal sealed class UpdateOperationStatusCommand
{
    public async ValueTask<bool> ExecuteAsync(
        SqliteConnection connection,
        Guid operationId,
        WorkflowOperationStatus status,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = SqliteStoreSupport.CreateCommand(connection, null, """
            UPDATE workflow_run_operations
            SET status = $status, updated_at = $now
            WHERE operation_id = $operationId;
            """);
        command.Parameters.AddWithValue("$status", (int)status);
        command.Parameters.AddWithValue("$now", SqliteStoreSupport.FormatTimestamp(now));
        command.Parameters.AddWithValue(
            "$operationId",
            SqliteStoreSupport.Format(operationId));
        return await command.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false) == 1;
    }
}
