using Microsoft.Data.Sqlite;

namespace Penghou.Zhinu.Sqlite.Persistence.Steps;

internal sealed class SkipCompensationCommand
{
    public async ValueTask ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid workflowRunId,
        string stepKey,
        int revision,
        CancellationToken cancellationToken)
    {
        await using var command = SqliteStoreSupport.CreateCommand(
            connection,
            transaction,
            """
            UPDATE workflow_step_compensations
            SET status = $skipped
            WHERE workflow_run_id = $runId AND step_key = $stepKey
              AND revision = $revision AND status = $pending;
            """);
        command.Parameters.AddWithValue("$skipped", (int)CompensationStatus.Skipped);
        command.Parameters.AddWithValue("$pending", (int)CompensationStatus.Pending);
        command.Parameters.AddWithValue("$runId", SqliteStoreSupport.Format(workflowRunId));
        command.Parameters.AddWithValue("$stepKey", stepKey);
        command.Parameters.AddWithValue("$revision", revision);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
