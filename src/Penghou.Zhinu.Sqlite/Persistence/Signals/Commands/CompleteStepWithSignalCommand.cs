using Microsoft.Data.Sqlite;

namespace Penghou.Zhinu.Sqlite.Persistence.Signals;

internal sealed class CompleteStepWithSignalCommand
{
    public async ValueTask ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid stepId,
        string? outputJson,
        string signalName,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = SqliteStoreSupport.CreateCommand(connection, transaction, """
            UPDATE workflow_steps
            SET status = $completed, output_json = $outputJson,
                signal_name = $name, completed_at = $now,
                available_at = NULL, error_json = NULL,
                lease_owner = NULL, lease_expires_at = NULL
            WHERE id = $id;
            """);
        command.Parameters.AddWithValue("$completed", (int)StepStatus.Completed);
        command.Parameters.AddWithValue("$outputJson", SqliteStoreSupport.DbValue(outputJson));
        command.Parameters.AddWithValue("$name", signalName);
        command.Parameters.AddWithValue("$now", SqliteStoreSupport.FormatTimestamp(now));
        command.Parameters.AddWithValue("$id", SqliteStoreSupport.Format(stepId));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
