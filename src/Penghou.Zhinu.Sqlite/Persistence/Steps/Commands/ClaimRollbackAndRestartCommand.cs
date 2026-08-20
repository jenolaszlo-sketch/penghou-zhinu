using Microsoft.Data.Sqlite;

namespace Penghou.Zhinu.Sqlite.Persistence.Steps;

internal sealed class ClaimRollbackAndRestartCommand
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
            SET status = $rollingBack, updated_at = $now,
                lease_owner = $owner, lease_expires_at = $expires,
                lease_generation = lease_generation + 1
            WHERE id = $id
              AND status IN ($completed, $failed, $rollingBack)
              AND (lease_expires_at IS NULL OR lease_expires_at <= $now)
            RETURNING lease_generation;
            """);
        command.Parameters.AddWithValue("$rollingBack", (int)WorkflowStatus.RollingBack);
        command.Parameters.AddWithValue("$now", SqliteStoreSupport.FormatTimestamp(now));
        command.Parameters.AddWithValue("$owner", ownerId);
        command.Parameters.AddWithValue("$expires", SqliteStoreSupport.FormatTimestamp(leaseExpiresAt));
        command.Parameters.AddWithValue("$id", SqliteStoreSupport.Format(workflowRunId));
        command.Parameters.AddWithValue("$completed", (int)WorkflowStatus.Completed);
        command.Parameters.AddWithValue("$failed", (int)WorkflowStatus.Failed);
        var generation = await command.ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false);
        return generation is null ? null : (long)generation;
    }
}
