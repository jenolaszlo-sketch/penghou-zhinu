using Microsoft.Data.Sqlite;

namespace Penghou.Zhinu.Sqlite.Persistence.Signals;

internal sealed class InsertSignalCommand
{
    public async ValueTask ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid signalId,
        Guid workflowRunId,
        string signalName,
        string? dataJson,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = SqliteStoreSupport.CreateCommand(connection, transaction, """
            INSERT INTO workflow_signals
            (id, workflow_run_id, signal_name, data_json, created_at)
            VALUES ($id, $runId, $name, $dataJson, $now);
            """);
        command.Parameters.AddWithValue("$id", SqliteStoreSupport.Format(signalId));
        command.Parameters.AddWithValue("$runId", SqliteStoreSupport.Format(workflowRunId));
        command.Parameters.AddWithValue("$name", signalName);
        command.Parameters.AddWithValue("$dataJson", SqliteStoreSupport.DbValue(dataJson));
        command.Parameters.AddWithValue("$now", SqliteStoreSupport.FormatTimestamp(now));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
