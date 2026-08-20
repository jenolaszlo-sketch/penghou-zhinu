using Microsoft.Data.Sqlite;

namespace Penghou.Zhinu.Sqlite.Persistence.Leases;

internal sealed class RenewStepLeaseCommand
{
    public async ValueTask<bool> ExecuteAsync(
        SqliteConnection connection,
        Guid stepId,
        string ownerId,
        DateTimeOffset leaseExpiresAt,
        CancellationToken cancellationToken)
    {
        await using var command = SqliteStoreSupport.CreateCommand(connection, null, """
            UPDATE workflow_steps
            SET lease_expires_at = $expires
            WHERE id = $id AND lease_owner = $owner AND status = $running
              AND lease_generation = (SELECT lease_generation FROM workflow_runs
                                      WHERE id = workflow_steps.workflow_run_id);
            """);
        command.Parameters.AddWithValue("$expires", SqliteStoreSupport.FormatTimestamp(leaseExpiresAt));
        command.Parameters.AddWithValue("$id", SqliteStoreSupport.Format(stepId));
        command.Parameters.AddWithValue("$owner", ownerId);
        command.Parameters.AddWithValue("$running", (int)StepStatus.Running);
        return await command.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false) == 1;
    }
}
