using Microsoft.Data.Sqlite;

namespace Penghou.Zhinu.Sqlite.Persistence.Steps;

internal sealed class ReleaseRollbackLeaseCommand
{
    public async ValueTask ExecuteAsync(
        SqliteConnection connection,
        Guid workflowRunId,
        string ownerId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = SqliteStoreSupport.CreateCommand(connection, null, """
            UPDATE workflow_runs
            SET lease_owner = NULL, lease_expires_at = NULL, updated_at = $now
            WHERE id = $id AND lease_owner = $owner
              AND status IN ($completed, $failed);
            """);
        command.Parameters.AddWithValue("$now", SqliteStoreSupport.FormatTimestamp(now));
        command.Parameters.AddWithValue("$id", SqliteStoreSupport.Format(workflowRunId));
        command.Parameters.AddWithValue("$owner", ownerId);
        command.Parameters.AddWithValue("$completed", (int)WorkflowStatus.Completed);
        command.Parameters.AddWithValue("$failed", (int)WorkflowStatus.Failed);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
