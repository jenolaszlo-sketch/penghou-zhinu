using Microsoft.Data.Sqlite;

namespace Penghou.Zhinu.Sqlite.Persistence.Steps;

internal sealed class ResetRunForRestartCommand
{
    public async ValueTask ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid workflowRunId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = SqliteStoreSupport.CreateCommand(connection, transaction, """
            UPDATE workflow_runs
            SET status = $pending, output_json = NULL,
                error_json = NULL, completed_at = NULL,
                lease_owner = NULL, lease_expires_at = NULL, updated_at = $now
            WHERE id = $id;
            """);
        command.Parameters.AddWithValue("$pending", (int)WorkflowStatus.Pending);
        command.Parameters.AddWithValue("$now", SqliteStoreSupport.FormatTimestamp(now));
        command.Parameters.AddWithValue("$id", SqliteStoreSupport.Format(workflowRunId));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
