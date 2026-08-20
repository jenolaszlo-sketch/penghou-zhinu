using Microsoft.Data.Sqlite;

namespace Penghou.Zhinu.Sqlite.Persistence.Steps;

internal sealed class FailRollbackAndRestartCommand
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
                completed_at = $now, updated_at = $now,
                lease_owner = NULL, lease_expires_at = NULL
            WHERE id = $id AND lease_owner = $owner
              AND status = $rollingBack AND lease_generation = $generation;
            """);
        command.Parameters.AddWithValue("$failed", (int)WorkflowStatus.Failed);
        command.Parameters.AddWithValue(
            "$errorJson",
            SqliteStoreSupport.DbValue(SqliteStoreSupport.SerializeError(error)));
        command.Parameters.AddWithValue("$now", SqliteStoreSupport.FormatTimestamp(now));
        command.Parameters.AddWithValue("$id", SqliteStoreSupport.Format(workflowRunId));
        command.Parameters.AddWithValue("$owner", ownerId);
        command.Parameters.AddWithValue("$rollingBack", (int)WorkflowStatus.RollingBack);
        command.Parameters.AddWithValue("$generation", generation);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
