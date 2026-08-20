using Microsoft.Data.Sqlite;

namespace Penghou.Zhinu.Sqlite.Persistence.Steps;

internal sealed class RecordCompensationInputCommand
{
    public async ValueTask ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid workflowRunId,
        string stepKey,
        int revision,
        string? inputJson,
        string? inputType,
        CancellationToken cancellationToken)
    {
        await using var command = SqliteStoreSupport.CreateCommand(
            connection,
            transaction,
            """
            UPDATE workflow_step_compensations
            SET input_json = $inputJson, input_type = $inputType
            WHERE workflow_run_id = $runId AND step_key = $stepKey
              AND revision = $revision;
            """);
        command.Parameters.AddWithValue("$inputJson", SqliteStoreSupport.DbValue(inputJson));
        command.Parameters.AddWithValue("$inputType", SqliteStoreSupport.DbValue(inputType));
        command.Parameters.AddWithValue("$runId", SqliteStoreSupport.Format(workflowRunId));
        command.Parameters.AddWithValue("$stepKey", stepKey);
        command.Parameters.AddWithValue("$revision", revision);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
