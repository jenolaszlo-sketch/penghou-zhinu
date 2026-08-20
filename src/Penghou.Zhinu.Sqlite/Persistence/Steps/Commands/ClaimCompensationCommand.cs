using Microsoft.Data.Sqlite;

namespace Penghou.Zhinu.Sqlite.Persistence.Steps;

internal sealed class ClaimCompensationCommand
{
    public async ValueTask<WorkflowStepCompensation?> ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid workflowRunId,
        string stepKey,
        string ownerId,
        long generation,
        DateTimeOffset now,
        DateTimeOffset leaseExpiresAt,
        string? actor,
        string? reason,
        CancellationToken cancellationToken)
    {
        await using var command = SqliteStoreSupport.CreateCommand(connection, transaction, $"""
            UPDATE workflow_step_compensations
            SET status = $running, attempt = attempt + 1,
                started_at = $now, lease_owner = $owner,
                lease_expires_at = $expires, lease_generation = $generation,
                actor = $actor, reason = $reason,
                available_at = NULL, error_json = NULL
            WHERE workflow_run_id = $runId AND step_key = $stepKey
              AND status IN ($pending, $failed)
              AND (available_at IS NULL OR available_at <= $now)
              AND lease_generation <= $generation
            RETURNING {SqliteStoreSupport.CompensationColumns};
            """);
        command.Parameters.AddWithValue("$running", (int)CompensationStatus.Running);
        command.Parameters.AddWithValue("$pending", (int)CompensationStatus.Pending);
        command.Parameters.AddWithValue("$failed", (int)CompensationStatus.Failed);
        command.Parameters.AddWithValue("$runId", SqliteStoreSupport.Format(workflowRunId));
        command.Parameters.AddWithValue("$stepKey", stepKey);
        command.Parameters.AddWithValue("$owner", ownerId);
        command.Parameters.AddWithValue("$expires", SqliteStoreSupport.FormatTimestamp(leaseExpiresAt));
        command.Parameters.AddWithValue("$generation", generation);
        command.Parameters.AddWithValue("$actor", SqliteStoreSupport.DbValue(actor));
        command.Parameters.AddWithValue("$reason", SqliteStoreSupport.DbValue(reason));
        command.Parameters.AddWithValue("$now", SqliteStoreSupport.FormatTimestamp(now));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? SqliteStoreSupport.ReadCompensation(reader)
            : null;
    }
}
