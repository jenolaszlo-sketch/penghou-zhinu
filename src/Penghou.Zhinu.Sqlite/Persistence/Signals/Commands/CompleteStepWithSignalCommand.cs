using Microsoft.Data.Sqlite;

namespace Penghou.Zhinu.Sqlite.Persistence.Signals;

internal sealed class CompleteStepWithSignalCommand
{
    /// <summary>
    /// Completes a waiting step with a delivered signal. The update is fenced
    /// by step revision, and by lease owner unless the step is unowned or its
    /// lease lapsed (waiting steps do not renew leases). Generation fencing
    /// happens in the caller, which compares the caller generation against
    /// the run. Returns the number of rows completed (1) or 0 when the step
    /// was superseded or ownership moved underneath the caller.
    /// </summary>
    public async ValueTask<int> ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid stepId,
        int revision,
        string ownerId,
        string? outputJson,
        string signalName,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = SqliteStoreSupport.CreateCommand(connection, transaction, """
            UPDATE workflow_steps
            SET status = $completed, output_json = $outputJson,
                signal_name = $name, completed_at = $now,
                available_at = NULL, error_json = NULL,
                lease_owner = NULL, lease_expires_at = NULL
            WHERE id = $id AND revision = $revision
                AND (lease_owner = $owner OR lease_owner IS NULL OR lease_expires_at <= $now);
            """);
        command.Parameters.AddWithValue("$completed", (int)StepStatus.Completed);
        command.Parameters.AddWithValue("$outputJson", SqliteStoreSupport.DbValue(outputJson));
        command.Parameters.AddWithValue("$name", signalName);
        command.Parameters.AddWithValue("$now", SqliteStoreSupport.FormatTimestamp(now));
        command.Parameters.AddWithValue("$id", SqliteStoreSupport.Format(stepId));
        command.Parameters.AddWithValue("$revision", revision);
        command.Parameters.AddWithValue("$owner", ownerId);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
