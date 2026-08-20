using Microsoft.Data.Sqlite;

namespace Penghou.Zhinu.Sqlite.Persistence.Steps;

internal sealed class InsertStepDependencyCommand
{
    public async ValueTask ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid workflowRunId,
        string stepKey,
        string dependencyStepKey,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = SqliteStoreSupport.CreateCommand(connection, transaction, """
            INSERT OR IGNORE INTO workflow_step_dependencies
            (run_id, step_key, depends_on_step_key, created_at)
            VALUES ($runId, $stepKey, $dependsOn, $now);
            """);
        command.Parameters.AddWithValue("$runId", SqliteStoreSupport.Format(workflowRunId));
        command.Parameters.AddWithValue("$stepKey", stepKey);
        command.Parameters.AddWithValue("$dependsOn", dependencyStepKey);
        command.Parameters.AddWithValue("$now", SqliteStoreSupport.FormatTimestamp(now));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
