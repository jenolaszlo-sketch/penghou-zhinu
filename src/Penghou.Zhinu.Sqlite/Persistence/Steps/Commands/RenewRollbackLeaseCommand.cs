using Microsoft.Data.Sqlite;

namespace Penghou.Zhinu.Sqlite.Persistence.Steps;

internal sealed class RenewRollbackLeaseCommand
{
    public async ValueTask<bool> ExecuteAsync(
        SqliteConnection connection,
        Guid workflowRunId,
        string ownerId,
        DateTimeOffset leaseExpiresAt,
        CancellationToken cancellationToken)
    {
        await using var command = SqliteStoreSupport.CreateCommand(connection, null, """
            UPDATE workflow_runs
            SET lease_expires_at = $expires
            WHERE id = $id AND lease_owner = $owner
              AND status IN ($completed, $failed);
            """);
        command.Parameters.AddWithValue("$expires", SqliteStoreSupport.FormatTimestamp(leaseExpiresAt));
        command.Parameters.AddWithValue("$id", SqliteStoreSupport.Format(workflowRunId));
        command.Parameters.AddWithValue("$owner", ownerId);
        command.Parameters.AddWithValue("$completed", (int)WorkflowStatus.Completed);
        command.Parameters.AddWithValue("$failed", (int)WorkflowStatus.Failed);
        return await command.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false) == 1;
    }
}
