using Microsoft.Data.Sqlite;

namespace Penghou.Zhinu.Sqlite.Persistence.Steps;

internal sealed class GetStepQuery
{
    public async ValueTask<WorkflowStepRun?> ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid workflowRunId,
        string stepKey,
        CancellationToken cancellationToken)
    {
        await using var command = SqliteStoreSupport.CreateCommand(connection, transaction, $"""
            SELECT {SqliteStoreSupport.StepColumns}
            FROM workflow_steps
            WHERE workflow_run_id = $runId AND step_key = $stepKey
            ORDER BY revision DESC
            LIMIT 1;
            """);
        command.Parameters.AddWithValue("$runId", SqliteStoreSupport.Format(workflowRunId));
        command.Parameters.AddWithValue("$stepKey", stepKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? SqliteStoreSupport.ReadStep(reader)
            : null;
    }
}
