using Microsoft.Data.Sqlite;

namespace Penghou.Zhinu.Sqlite.Persistence.Steps;

internal sealed class FailCompensationCommand
{
    public async ValueTask<int> ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid compensationId,
        string ownerId,
        WorkflowError error,
        DateTimeOffset? retryAt,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = SqliteStoreSupport.CreateCommand(connection, transaction, """
            UPDATE workflow_step_compensations
            SET status = $failed, error_json = $errorJson,
                completed_at = $now, available_at = $retryAt,
                lease_owner = NULL, lease_expires_at = NULL
            WHERE id = $id AND status = $running AND lease_owner = $owner;
            """);
        command.Parameters.AddWithValue("$failed", (int)CompensationStatus.Failed);
        command.Parameters.AddWithValue(
            "$errorJson",
            SqliteStoreSupport.DbValue(SqliteStoreSupport.SerializeError(error)));
        command.Parameters.AddWithValue("$now", SqliteStoreSupport.FormatTimestamp(now));
        command.Parameters.AddWithValue(
            "$retryAt",
            SqliteStoreSupport.DbValue(SqliteStoreSupport.FormatNullable(retryAt)));
        command.Parameters.AddWithValue("$id", SqliteStoreSupport.Format(compensationId));
        command.Parameters.AddWithValue("$running", (int)CompensationStatus.Running);
        command.Parameters.AddWithValue("$owner", ownerId);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
