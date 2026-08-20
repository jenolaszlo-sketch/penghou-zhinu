using Microsoft.Data.Sqlite;

namespace Penghou.Zhinu.Sqlite.Persistence.Workflows;

internal sealed class CancelRunCommand
{
    public async ValueTask<int> ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid workflowRunId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = SqliteStoreSupport.CreateCommand(connection, transaction, """
            UPDATE workflow_runs
            SET status = $cancelled, updated_at = $now, completed_at = $now,
                lease_owner = NULL, lease_expires_at = NULL
            WHERE id = $id AND status NOT IN ($completed, $failed, $cancelled, $compensated, $rollingBack);
            """);
        command.Parameters.AddWithValue("$cancelled", (int)WorkflowStatus.Cancelled);
        command.Parameters.AddWithValue("$completed", (int)WorkflowStatus.Completed);
        command.Parameters.AddWithValue("$failed", (int)WorkflowStatus.Failed);
        command.Parameters.AddWithValue("$compensated", (int)WorkflowStatus.Compensated);
        command.Parameters.AddWithValue("$rollingBack", (int)WorkflowStatus.RollingBack);
        command.Parameters.AddWithValue("$now", SqliteStoreSupport.FormatTimestamp(now));
        command.Parameters.AddWithValue("$id", SqliteStoreSupport.Format(workflowRunId));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
