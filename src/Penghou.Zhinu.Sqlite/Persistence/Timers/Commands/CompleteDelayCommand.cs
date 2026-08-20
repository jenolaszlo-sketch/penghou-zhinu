using Microsoft.Data.Sqlite;

namespace Penghou.Zhinu.Sqlite.Persistence.Timers;

internal sealed class CompleteDelayCommand
{
    public async ValueTask<int> ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid stepId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = SqliteStoreSupport.CreateCommand(connection, transaction, """
            UPDATE workflow_steps
            SET status = $completed, completed_at = $now,
                lease_owner = NULL, lease_expires_at = NULL
            WHERE id = $id AND status = $waiting AND available_at <= $now
              AND lease_generation = (SELECT lease_generation FROM workflow_runs
                                      WHERE id = workflow_steps.workflow_run_id);
            """);
        command.Parameters.AddWithValue("$completed", (int)StepStatus.Completed);
        command.Parameters.AddWithValue("$waiting", (int)StepStatus.Waiting);
        command.Parameters.AddWithValue("$now", SqliteStoreSupport.FormatTimestamp(now));
        command.Parameters.AddWithValue("$id", SqliteStoreSupport.Format(stepId));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
