using Microsoft.Data.Sqlite;

namespace Penghou.Zhinu.Sqlite.Persistence.Leases;

internal sealed class GetRunnableRunIdsQuery
{
    public async ValueTask<IReadOnlyList<Guid>> ExecuteAsync(
        SqliteConnection connection,
        DateTimeOffset now,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var command = SqliteStoreSupport.CreateCommand(connection, null, """
            SELECT id
            FROM workflow_runs
            WHERE status IN ($pending, $running, $rollingBack)
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
              -- Fully parked runs stay out until a wait becomes ready or due:
              -- parked signal/child waits wake via ready flips, timed waits via
              -- their due time. Runs without waits keep historical behavior.
              AND (
                NOT EXISTS (
                    SELECT 1 FROM workflow_waits
                    WHERE workflow_waits.workflow_run_id = workflow_runs.id
                      AND status = $parked)
                OR EXISTS (
                    SELECT 1 FROM workflow_waits
                    WHERE workflow_waits.workflow_run_id = workflow_runs.id
                      AND status = $ready)
                OR EXISTS (
                    SELECT 1 FROM workflow_waits
                    WHERE workflow_waits.workflow_run_id = workflow_runs.id
                      AND status = $parked
                      AND available_at IS NOT NULL
                      AND available_at <= $now))
            ORDER BY created_at, id
            LIMIT $limit;
            """);
        command.Parameters.AddWithValue("$pending", (int)WorkflowStatus.Pending);
        command.Parameters.AddWithValue("$running", (int)WorkflowStatus.Running);
        command.Parameters.AddWithValue("$rollingBack", (int)WorkflowStatus.RollingBack);
        command.Parameters.AddWithValue("$parked", (int)WaitStatus.Parked);
        command.Parameters.AddWithValue("$ready", (int)WaitStatus.Ready);
        command.Parameters.AddWithValue("$now", SqliteStoreSupport.FormatTimestamp(now));
        command.Parameters.AddWithValue("$limit", limit);
        var results = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            results.Add(Guid.Parse(reader.GetString(0)));
        return results;
    }
}
