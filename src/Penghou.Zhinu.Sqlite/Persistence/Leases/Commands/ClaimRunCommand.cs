using Microsoft.Data.Sqlite;

namespace Penghou.Zhinu.Sqlite.Persistence.Leases;

internal sealed class ClaimRunCommand
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
            SET status = $running,
                updated_at = $now,
                lease_owner = $owner,
                lease_expires_at = $expires,
                lease_generation = lease_generation + 1
            WHERE id = $id
              AND status IN ($pending, $running)
              AND (lease_expires_at IS NULL OR lease_expires_at <= $now)
            RETURNING lease_generation;
            """);
        command.Parameters.AddWithValue("$running", (int)WorkflowStatus.Running);
        command.Parameters.AddWithValue("$pending", (int)WorkflowStatus.Pending);
        command.Parameters.AddWithValue("$now", SqliteStoreSupport.FormatTimestamp(now));
        command.Parameters.AddWithValue("$owner", ownerId);
        command.Parameters.AddWithValue("$expires", SqliteStoreSupport.FormatTimestamp(leaseExpiresAt));
        command.Parameters.AddWithValue("$id", SqliteStoreSupport.Format(workflowRunId));
        var generation = await command.ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false);
        return generation is null ? null : (long)generation;
    }
}
