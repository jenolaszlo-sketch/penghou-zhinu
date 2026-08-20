using Microsoft.Data.Sqlite;

namespace Penghou.Zhinu.Sqlite.Persistence.Signals;

internal sealed class MarkSignalDeliveredCommand
{
    public async ValueTask<int> ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string signalId,
        Guid stepId,
        CancellationToken cancellationToken)
    {
        await using var command = SqliteStoreSupport.CreateCommand(connection, transaction, """
            UPDATE workflow_signals
            SET delivered_step_id = $stepId
            WHERE id = $id AND delivered_step_id IS NULL;
            """);
        command.Parameters.AddWithValue("$stepId", SqliteStoreSupport.Format(stepId));
        command.Parameters.AddWithValue("$id", signalId);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
