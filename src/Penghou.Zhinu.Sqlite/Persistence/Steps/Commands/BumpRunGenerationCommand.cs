using Microsoft.Data.Sqlite;

namespace Penghou.Zhinu.Sqlite.Persistence.Steps;

internal sealed class BumpRunGenerationCommand
{
    public async ValueTask ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid workflowRunId,
        CancellationToken cancellationToken)
    {
        await using var command = SqliteStoreSupport.CreateCommand(connection, transaction, """
            UPDATE workflow_runs
            SET lease_generation = lease_generation + 1
            WHERE id = $id;
            """);
        command.Parameters.AddWithValue("$id", SqliteStoreSupport.Format(workflowRunId));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
