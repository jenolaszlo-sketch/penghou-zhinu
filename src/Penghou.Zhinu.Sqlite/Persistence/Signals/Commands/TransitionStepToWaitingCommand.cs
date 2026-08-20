using Microsoft.Data.Sqlite;

namespace Penghou.Zhinu.Sqlite.Persistence.Signals;

internal sealed class TransitionStepToWaitingCommand
{
    public async ValueTask ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid stepId,
        string signalName,
        CancellationToken cancellationToken)
    {
        await using var command = SqliteStoreSupport.CreateCommand(connection, transaction, """
            UPDATE workflow_steps
            SET status = $waiting, signal_name = $name,
                available_at = NULL, error_json = NULL,
                lease_owner = NULL, lease_expires_at = NULL
            WHERE id = $id AND status = $running;
            """);
        command.Parameters.AddWithValue("$waiting", (int)StepStatus.Waiting);
        command.Parameters.AddWithValue("$running", (int)StepStatus.Running);
        command.Parameters.AddWithValue("$name", signalName);
        command.Parameters.AddWithValue("$id", SqliteStoreSupport.Format(stepId));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
