using Microsoft.Data.Sqlite;

namespace Penghou.Zhinu.Sqlite.Persistence.Leases;

internal sealed class ClaimRunCommand
{
    public async ValueTask<long?> ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid workflowRunId,
        string ownerId,
        DateTimeOffset now,
        DateTimeOffset leaseExpiresAt,
        CancellationToken cancellationToken)
    {
        await using var command = SqliteStoreSupport.CreateCommand(connection, transaction, """
            UPDATE workflow_runs
            SET status = $running,
                updated_at = $now,
                lease_owner = $owner,
                lease_expires_at = $expires,
                lease_generation = lease_generation + 1
            WHERE id = $id
              AND status IN ($pending, $running)
              AND (lease_expires_at IS NULL OR lease_expires_at <= $now)
              AND (NOT EXISTS (
                SELECT 1 FROM workflow_authorization_checkpoints p
                WHERE p.workflow_run_id=workflow_runs.id AND p.ready=0 AND p.decision=3
                  AND p.lease_generation=workflow_runs.lease_generation
                  AND ((p.is_compensation=0 AND EXISTS (SELECT 1 FROM workflow_steps s
                    WHERE s.id=p.claim_id AND s.revision=p.revision AND s.lease_generation=p.lease_generation))
                    OR (p.is_compensation=1 AND EXISTS (SELECT 1 FROM workflow_step_compensations c
                    WHERE c.id=p.claim_id AND c.revision=p.revision AND c.lease_generation=p.lease_generation)))
              ) OR EXISTS (
                SELECT 1 FROM workflow_authorization_checkpoints p
                WHERE p.workflow_run_id=workflow_runs.id AND p.ready=1 AND p.decision=3
                  AND p.lease_generation=workflow_runs.lease_generation
                  AND ((p.is_compensation=0 AND EXISTS (SELECT 1 FROM workflow_steps s
                    WHERE s.id=p.claim_id AND s.revision=p.revision AND s.lease_generation=p.lease_generation))
                    OR (p.is_compensation=1 AND EXISTS (SELECT 1 FROM workflow_step_compensations c
                    WHERE c.id=p.claim_id AND c.revision=p.revision AND c.lease_generation=p.lease_generation)))
              ))
              AND (authorization_provider_id IS NULL
                OR EXISTS (SELECT 1 FROM workflow_generations g
                    JOIN workflow_instances i ON i.instance_id=g.instance_id
                    WHERE g.workflow_run_id=workflow_runs.id
                      AND (g.status=2 OR (workflow_runs.status=$rollingBack AND g.status=5))))
            RETURNING lease_generation;
            """);
        command.Parameters.AddWithValue("$running", (int)WorkflowStatus.Running);
        command.Parameters.AddWithValue("$rollingBack", (int)WorkflowStatus.RollingBack);
        command.Parameters.AddWithValue("$pending", (int)WorkflowStatus.Pending);
        command.Parameters.AddWithValue("$now", SqliteStoreSupport.FormatTimestamp(now));
        command.Parameters.AddWithValue("$owner", ownerId);
        command.Parameters.AddWithValue("$expires", SqliteStoreSupport.FormatTimestamp(leaseExpiresAt));
        command.Parameters.AddWithValue("$id", SqliteStoreSupport.Format(workflowRunId));
        var generation = await command.ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false);
        return generation is null ? null : (long)generation;
    }
}
