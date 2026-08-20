using Microsoft.Data.Sqlite;

namespace Penghou.Zhinu.Sqlite.Persistence.Steps;

internal sealed class CompleteRollbackCommand
{
    public async ValueTask<int> ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid workflowRunId,
        string ownerId,
        long generation,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = SqliteStoreSupport.CreateCommand(connection, transaction, """
            UPDATE workflow_runs
            SET status = $compensated, completed_at = $now,
                lease_owner = NULL, lease_expires_at = NULL, updated_at = $now
            WHERE id = $id AND lease_owner = $owner
              AND status IN ($completed, $failed)
              AND lease_generation = $generation;
            """);
        command.Parameters.AddWithValue("$compensated", (int)WorkflowStatus.Compensated);
        command.Parameters.AddWithValue("$now", SqliteStoreSupport.FormatTimestamp(now));
        command.Parameters.AddWithValue("$id", SqliteStoreSupport.Format(workflowRunId));
        command.Parameters.AddWithValue("$owner", ownerId);
        command.Parameters.AddWithValue("$completed", (int)WorkflowStatus.Completed);
        command.Parameters.AddWithValue("$failed", (int)WorkflowStatus.Failed);
        command.Parameters.AddWithValue("$generation", generation);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
