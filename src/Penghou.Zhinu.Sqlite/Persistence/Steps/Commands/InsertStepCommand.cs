using Microsoft.Data.Sqlite;

namespace Penghou.Zhinu.Sqlite.Persistence.Steps;

internal sealed class InsertStepCommand
{
    public async ValueTask ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        WorkflowStepRun step,
        CancellationToken cancellationToken)
    {
        await using var command = SqliteStoreSupport.CreateCommand(connection, transaction, """
            INSERT INTO workflow_steps
            (id, workflow_run_id, step_key, status, attempt, input_json,
             input_type, input_hash, output_json, output_type, error_json,
             signal_name, created_at, started_at, completed_at, available_at,
             lease_owner, lease_expires_at, revision, lease_generation,
             implementation_key)
            VALUES
            ($id, $runId, $stepKey, $status, $attempt, $inputJson,
             $inputType, $inputHash, $outputJson, $outputType, $errorJson,
             $signalName, $createdAt, $startedAt, $completedAt, $availableAt,
             $leaseOwner, $leaseExpiresAt, $revision, $leaseGeneration,
             $implementationKey);
            """);
        command.Parameters.AddWithValue("$id", SqliteStoreSupport.Format(step.Id));
        command.Parameters.AddWithValue("$runId", SqliteStoreSupport.Format(step.WorkflowRunId));
        command.Parameters.AddWithValue("$stepKey", step.StepKey);
        command.Parameters.AddWithValue("$status", (int)step.Status);
        command.Parameters.AddWithValue("$attempt", step.Attempt);
        command.Parameters.AddWithValue("$inputJson", SqliteStoreSupport.DbValue(step.InputJson));
        command.Parameters.AddWithValue("$inputType", SqliteStoreSupport.DbValue(step.InputType));
        command.Parameters.AddWithValue("$inputHash", SqliteStoreSupport.DbValue(step.InputHash));
        command.Parameters.AddWithValue("$outputJson", SqliteStoreSupport.DbValue(step.OutputJson));
        command.Parameters.AddWithValue("$outputType", SqliteStoreSupport.DbValue(step.OutputType));
        command.Parameters.AddWithValue(
            "$errorJson",
            SqliteStoreSupport.DbValue(SqliteStoreSupport.SerializeError(step.Error)));
        command.Parameters.AddWithValue("$signalName", SqliteStoreSupport.DbValue(step.SignalName));
        command.Parameters.AddWithValue("$createdAt", SqliteStoreSupport.FormatTimestamp(step.CreatedAt));
        command.Parameters.AddWithValue(
            "$startedAt",
            SqliteStoreSupport.DbValue(SqliteStoreSupport.FormatNullable(step.StartedAt)));
        command.Parameters.AddWithValue(
            "$completedAt",
            SqliteStoreSupport.DbValue(SqliteStoreSupport.FormatNullable(step.CompletedAt)));
        command.Parameters.AddWithValue(
            "$availableAt",
            SqliteStoreSupport.DbValue(SqliteStoreSupport.FormatNullable(step.AvailableAt)));
        command.Parameters.AddWithValue("$leaseOwner", SqliteStoreSupport.DbValue(step.LeaseOwner));
        command.Parameters.AddWithValue(
            "$leaseExpiresAt",
            SqliteStoreSupport.DbValue(SqliteStoreSupport.FormatNullable(step.LeaseExpiresAt)));
        command.Parameters.AddWithValue("$revision", step.Revision);
        command.Parameters.AddWithValue("$leaseGeneration", step.LeaseGeneration);
        command.Parameters.AddWithValue(
            "$implementationKey",
            SqliteStoreSupport.DbValue(step.ImplementationKey));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
