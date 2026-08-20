using Microsoft.Data.Sqlite;

namespace Penghou.Zhinu.Sqlite.Persistence.Steps;

internal sealed class GetCurrentStepsQuery
{
    public async ValueTask<IReadOnlyList<WorkflowStepRun>> ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid workflowRunId,
        CancellationToken cancellationToken)
    {
        await using var command = SqliteStoreSupport.CreateCommand(connection, transaction, $"""
            SELECT {SqliteStoreSupport.StepColumns}
            FROM workflow_steps
            WHERE workflow_run_id = $runId
              AND revision = (
                  SELECT MAX(revision)
                  FROM workflow_steps current
                  WHERE current.workflow_run_id = workflow_steps.workflow_run_id
                    AND current.step_key = workflow_steps.step_key)
            ORDER BY created_at, step_key;
            """);
        command.Parameters.AddWithValue("$runId", SqliteStoreSupport.Format(workflowRunId));
        return await SqliteStoreSupport.ReadStepsAsync(command, cancellationToken)
            .ConfigureAwait(false);
    }
}
