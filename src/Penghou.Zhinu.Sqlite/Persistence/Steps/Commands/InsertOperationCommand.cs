using Microsoft.Data.Sqlite;

namespace Penghou.Zhinu.Sqlite.Persistence.Steps;

internal sealed class InsertOperationCommand
{
    public async ValueTask ExecuteAsync(
        SqliteConnection connection,
        WorkflowRunOperation operation,
        CancellationToken cancellationToken)
    {
        await using var command = SqliteStoreSupport.CreateCommand(connection, null, """
            INSERT INTO workflow_run_operations
            (operation_id, workflow_run_id, operation_type, status, payload_json,
             created_at, updated_at, completed_at)
            VALUES
            ($operationId, $runId, $operationType, $status, $payloadJson,
             $createdAt, $updatedAt, $completedAt);
            """);
        command.Parameters.AddWithValue(
            "$operationId",
            SqliteStoreSupport.Format(operation.OperationId));
        command.Parameters.AddWithValue(
            "$runId",
            SqliteStoreSupport.Format(operation.WorkflowRunId));
        command.Parameters.AddWithValue("$operationType", operation.OperationType);
        command.Parameters.AddWithValue("$status", (int)operation.Status);
        command.Parameters.AddWithValue(
            "$payloadJson",
            SqliteStoreSupport.DbValue(operation.PayloadJson));
        command.Parameters.AddWithValue(
            "$createdAt",
            SqliteStoreSupport.FormatTimestamp(operation.CreatedAt));
        command.Parameters.AddWithValue(
            "$updatedAt",
            SqliteStoreSupport.FormatTimestamp(operation.UpdatedAt));
        command.Parameters.AddWithValue(
            "$completedAt",
            SqliteStoreSupport.DbValue(SqliteStoreSupport.FormatNullable(operation.CompletedAt)));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
