using Microsoft.Data.Sqlite;

namespace Penghou.Zhinu.Sqlite.Persistence.Leases;

internal sealed class RecoverExpiredCompensationsCommand
{
    public async ValueTask ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = SqliteStoreSupport.CreateCommand(connection, transaction, """
            UPDATE workflow_step_compensations
            SET status = $pending, lease_owner = NULL, lease_expires_at = NULL
            WHERE status = $running AND lease_expires_at <= $now;
            """);
        command.Parameters.AddWithValue("$pending", (int)CompensationStatus.Pending);
        command.Parameters.AddWithValue("$running", (int)CompensationStatus.Running);
        command.Parameters.AddWithValue("$now", SqliteStoreSupport.FormatTimestamp(now));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
