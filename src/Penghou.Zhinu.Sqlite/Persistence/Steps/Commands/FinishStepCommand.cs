using Microsoft.Data.Sqlite;

namespace Penghou.Zhinu.Sqlite.Persistence.Steps;

internal sealed class FinishStepCommand
{
    public async ValueTask<int> ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid stepId,
        string ownerId,
        StepStatus status,
        string? outputJson,
        WorkflowError? error,
        DateTimeOffset? availableAt,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = SqliteStoreSupport.CreateCommand(connection, transaction, """
            UPDATE workflow_steps
            SET status = $status, output_json = $outputJson,
                error_json = $errorJson, available_at = $availableAt,
                completed_at = $completedAt,
                lease_owner = NULL, lease_expires_at = NULL
            WHERE id = $id AND status = $running AND lease_owner = $owner
              AND lease_generation = (SELECT lease_generation FROM workflow_runs
                                      WHERE id = workflow_steps.workflow_run_id);
            """);
        command.Parameters.AddWithValue("$status", (int)status);
        command.Parameters.AddWithValue("$outputJson", SqliteStoreSupport.DbValue(outputJson));
        command.Parameters.AddWithValue(
            "$errorJson",
            SqliteStoreSupport.DbValue(SqliteStoreSupport.SerializeError(error)));
        command.Parameters.AddWithValue(
            "$availableAt",
            SqliteStoreSupport.DbValue(SqliteStoreSupport.FormatNullable(availableAt)));
        command.Parameters.AddWithValue(
            "$completedAt",
            status is StepStatus.Completed or StepStatus.Failed
                ? SqliteStoreSupport.FormatTimestamp(now)
                : DBNull.Value);
        command.Parameters.AddWithValue("$id", SqliteStoreSupport.Format(stepId));
        command.Parameters.AddWithValue("$running", (int)StepStatus.Running);
        command.Parameters.AddWithValue("$owner", ownerId);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
