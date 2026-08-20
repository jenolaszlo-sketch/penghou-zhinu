using Microsoft.Data.Sqlite;

namespace Penghou.Zhinu.Sqlite.Persistence.Steps;

internal sealed class ClaimRollbackCommand
{
    public async ValueTask<long?> ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid workflowRunId,
        string ownerId,
        DateTimeOffset now,
        DateTimeOffset leaseExpiresAt,
        CancellationToken cancellationToken)
    {
        await using var command = SqliteStoreSupport.CreateCommand(connection, transaction, """
            UPDATE workflow_runs
            SET lease_owner = $owner, lease_expires_at = $expires,
                updated_at = $now
            WHERE id = $id AND status IN ($completed, $failed)
              AND (lease_expires_at IS NULL OR lease_expires_at <= $now)
            RETURNING lease_generation;
            """);
        command.Parameters.AddWithValue("$owner", ownerId);
        command.Parameters.AddWithValue("$expires", SqliteStoreSupport.FormatTimestamp(leaseExpiresAt));
        command.Parameters.AddWithValue("$now", SqliteStoreSupport.FormatTimestamp(now));
        command.Parameters.AddWithValue("$id", SqliteStoreSupport.Format(workflowRunId));
        command.Parameters.AddWithValue("$completed", (int)WorkflowStatus.Completed);
        command.Parameters.AddWithValue("$failed", (int)WorkflowStatus.Failed);
        var generation = await command.ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false);
        return generation is null ? null : (long)generation;
    }
}
