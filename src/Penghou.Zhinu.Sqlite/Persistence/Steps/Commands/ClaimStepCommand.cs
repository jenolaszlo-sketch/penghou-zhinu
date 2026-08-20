using Microsoft.Data.Sqlite;

namespace Penghou.Zhinu.Sqlite.Persistence.Steps;

internal sealed class ClaimStepCommand
{
    public async ValueTask ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid stepId,
        string ownerId,
        int attempt,
        StepStatus status,
        DateTimeOffset now,
        DateTimeOffset leaseExpiresAt,
        long leaseGeneration,
        CancellationToken cancellationToken)
    {
        await using var command = SqliteStoreSupport.CreateCommand(connection, transaction, """
            UPDATE workflow_steps
            SET status = $running, attempt = $attempt, started_at = $now,
                available_at = NULL, error_json = NULL,
                lease_owner = $owner, lease_expires_at = $expires,
                lease_generation = $leaseGeneration
            WHERE id = $id;
            """);
        command.Parameters.AddWithValue("$running", (int)status);
        command.Parameters.AddWithValue("$attempt", attempt);
        command.Parameters.AddWithValue("$now", SqliteStoreSupport.FormatTimestamp(now));
        command.Parameters.AddWithValue("$owner", ownerId);
        command.Parameters.AddWithValue("$expires", SqliteStoreSupport.FormatTimestamp(leaseExpiresAt));
        command.Parameters.AddWithValue("$leaseGeneration", leaseGeneration);
        command.Parameters.AddWithValue("$id", SqliteStoreSupport.Format(stepId));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
