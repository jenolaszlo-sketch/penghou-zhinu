using Microsoft.Data.Sqlite;

namespace Penghou.Zhinu.Sqlite.Persistence.Steps;

internal sealed class InsertCompensationCommand
{
    public async ValueTask ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid workflowRunId,
        string stepKey,
        CompensationMetadata compensation,
        int revision,
        long leaseGeneration,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = SqliteStoreSupport.CreateCommand(connection, transaction, """
            INSERT INTO workflow_step_compensations
            (id, workflow_run_id, step_key, revision, compensation_name, status,
             attempt, retry_policy_json, timeout_ticks, idempotency_key,
             lease_generation, created_at, authorization_declaration_json)
            VALUES
            ($id, $runId, $stepKey, $revision, $name, $pending, 0,
             $retryJson, $timeoutTicks, $idempotencyKey, $generation, $createdAt, $authorizationDeclarationJson)
            ON CONFLICT(workflow_run_id, step_key, revision) DO UPDATE
            SET status = $pending, compensation_name = $name,
                retry_policy_json = $retryJson, timeout_ticks = $timeoutTicks,
                lease_generation = $generation,
                authorization_declaration_json = $authorizationDeclarationJson;
            """);
        command.Parameters.AddWithValue("$id", SqliteStoreSupport.Format(Guid.NewGuid()));
        command.Parameters.AddWithValue("$runId", SqliteStoreSupport.Format(workflowRunId));
        command.Parameters.AddWithValue("$stepKey", stepKey);
        command.Parameters.AddWithValue("$revision", revision);
        command.Parameters.AddWithValue("$name", compensation.Name);
        command.Parameters.AddWithValue("$pending", (int)CompensationStatus.Pending);
        command.Parameters.AddWithValue(
            "$retryJson",
            SqliteStoreSupport.DbValue(compensation.RetryPolicyJson));
        command.Parameters.AddWithValue(
            "$timeoutTicks",
            compensation.ExecutionTimeout is { } timeout
                ? timeout.Ticks
                : DBNull.Value);
        command.Parameters.AddWithValue(
            "$idempotencyKey",
            $"{workflowRunId:D}:{stepKey}:{revision}:compensation");
        command.Parameters.AddWithValue("$generation", leaseGeneration);
        command.Parameters.AddWithValue("$createdAt", SqliteStoreSupport.FormatTimestamp(now));
        command.Parameters.AddWithValue("$authorizationDeclarationJson", SqliteStoreSupport.DbValue(compensation.AuthorizationDeclarationJson));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
