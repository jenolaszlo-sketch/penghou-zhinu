using Microsoft.Data.Sqlite;

namespace Penghou.Zhinu.Sqlite.Persistence.Signals;

internal sealed class GetUndeliveredSignalQuery
{
    public async ValueTask<(string? SignalId, string? DataJson)> ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid workflowRunId,
        string signalName,
        CancellationToken cancellationToken)
    {
        await using var command = SqliteStoreSupport.CreateCommand(connection, transaction, """
            SELECT id, data_json
            FROM workflow_signals
            WHERE workflow_run_id = $runId
              AND signal_name = $name
              AND delivered_step_id IS NULL
            ORDER BY created_at
            LIMIT 1;
            """);
        command.Parameters.AddWithValue("$runId", SqliteStoreSupport.Format(workflowRunId));
        command.Parameters.AddWithValue("$name", signalName);
        string? signalId = null;
        string? dataJson = null;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                signalId = reader.GetString(0);
                dataJson = SqliteStoreSupport.GetNullableString(reader, 1);
            }
        }
        return (signalId, dataJson);
    }
}
