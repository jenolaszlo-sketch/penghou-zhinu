using Microsoft.Data.Sqlite;

namespace Penghou.Zhinu.Sqlite.Persistence.Workflows;

internal sealed class FinishRunCommand
{
    public async ValueTask<int> ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid workflowRunId,
        string ownerId,
        WorkflowStatus status,
        string? outputJson,
        string outputType,
        WorkflowError? error,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = SqliteStoreSupport.CreateCommand(connection, transaction, """
            UPDATE workflow_runs
            SET status = $status, output_json = $outputJson,
                output_type = CASE WHEN $outputType = '' THEN output_type ELSE $outputType END,
                error_json = $errorJson, updated_at = $now, completed_at = $now,
                lease_owner = NULL, lease_expires_at = NULL
            WHERE id = $id AND status = $running AND lease_owner = $owner;
            """);
        command.Parameters.AddWithValue("$status", (int)status);
        command.Parameters.AddWithValue("$outputJson", SqliteStoreSupport.DbValue(outputJson));
        command.Parameters.AddWithValue("$outputType", outputType);
        command.Parameters.AddWithValue(
            "$errorJson",
            SqliteStoreSupport.DbValue(SqliteStoreSupport.SerializeError(error)));
        command.Parameters.AddWithValue("$now", SqliteStoreSupport.FormatTimestamp(now));
        command.Parameters.AddWithValue("$id", SqliteStoreSupport.Format(workflowRunId));
        command.Parameters.AddWithValue("$running", (int)WorkflowStatus.Running);
        command.Parameters.AddWithValue("$owner", ownerId);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
