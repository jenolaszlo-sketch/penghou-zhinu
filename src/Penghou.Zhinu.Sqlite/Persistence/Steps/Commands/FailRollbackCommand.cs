using Microsoft.Data.Sqlite;

namespace Penghou.Zhinu.Sqlite.Persistence.Steps;

internal sealed class FailRollbackCommand
{
    public async ValueTask<int> ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid workflowRunId,
        string ownerId,
        long generation,
        WorkflowError error,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = SqliteStoreSupport.CreateCommand(connection, transaction, """
            UPDATE workflow_runs
            SET status = $failed, error_json = $errorJson,
                completed_at = $now, lease_owner = NULL,
                lease_expires_at = NULL, updated_at = $now
            WHERE id = $id AND lease_owner = $owner
              AND status IN ($completed, $failed)
              AND lease_generation = $generation;
            """);
        command.Parameters.AddWithValue("$failed", (int)WorkflowStatus.Failed);
        command.Parameters.AddWithValue(
            "$errorJson",
            SqliteStoreSupport.DbValue(SqliteStoreSupport.SerializeError(error)));
        command.Parameters.AddWithValue("$now", SqliteStoreSupport.FormatTimestamp(now));
        command.Parameters.AddWithValue("$id", SqliteStoreSupport.Format(workflowRunId));
        command.Parameters.AddWithValue("$owner", ownerId);
        command.Parameters.AddWithValue("$completed", (int)WorkflowStatus.Completed);
        command.Parameters.AddWithValue("$generation", generation);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
