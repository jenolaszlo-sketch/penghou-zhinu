using Microsoft.Data.Sqlite;

namespace Penghou.Zhinu.Sqlite.Persistence.Workflows;

internal sealed class UpdateRunMetadataCommand
{
    public async ValueTask<int> ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid workflowRunId,
        string? metadataJson,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = SqliteStoreSupport.CreateCommand(connection, transaction, """
            UPDATE workflow_runs
            SET metadata_json = $metadataJson, updated_at = $now
            WHERE id = $id;
            """);
        command.Parameters.AddWithValue("$metadataJson", SqliteStoreSupport.DbValue(metadataJson));
        command.Parameters.AddWithValue("$now", SqliteStoreSupport.FormatTimestamp(now));
        command.Parameters.AddWithValue("$id", SqliteStoreSupport.Format(workflowRunId));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
