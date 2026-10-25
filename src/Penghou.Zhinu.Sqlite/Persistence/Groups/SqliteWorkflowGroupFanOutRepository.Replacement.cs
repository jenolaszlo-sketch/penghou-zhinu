using System.Text.Json;
using Microsoft.Data.Sqlite;
using Penghou.Zhinu.Sqlite.Persistence.Workflows;

namespace Penghou.Zhinu.Sqlite.Persistence.Groups;

internal sealed partial class SqliteWorkflowGroupFanOutRepository
{
    private static async ValueTask EnsureOccurrenceNotRetiredAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid runId, ActivityGroupOccurrence occurrence, CancellationToken ct)
    {
        var chain = new List<string>();
        for (ActivityGroupOccurrence? current = occurrence; current is not null; current = current.Parent)
            chain.Add(current.GroupOccurrenceId);
        await using var command = SqliteStoreSupport.CreateCommand(connection, transaction, """
            SELECT 1 FROM workflow_group_fanout_retirements r JOIN workflow_group_fanout_items i
              ON i.workflow_run_id=r.workflow_run_id AND i.expansion_id=r.expansion_id
            WHERE r.workflow_run_id=$run AND i.group_occurrence_id IN (SELECT value FROM json_each($chain)) LIMIT 1;
            """);
        command.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(runId));
        command.Parameters.AddWithValue("$chain", JsonSerializer.Serialize(chain));
        if (await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null)
            throw new WorkflowStateException("A new step cannot join a retired group occurrence or its descendants.");
    }

    private static async ValueTask EnsureScopeNotRetiredAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid runId, string scopeKey, CancellationToken ct)
    {
        await using var command = SqliteStoreSupport.CreateCommand(connection, transaction, """
            SELECT 1 FROM workflow_group_fanout_retirements r
            JOIN workflow_group_fanout_expansions e ON e.workflow_run_id=r.workflow_run_id AND e.expansion_id=r.expansion_id
            WHERE e.workflow_run_id=$run AND e.fan_out_key=$key LIMIT 1;
            """);
        command.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(runId));
        command.Parameters.AddWithValue("$key", scopeKey);
        if (await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null)
            throw new WorkflowStateException("A retired fan-out scope cannot be admitted again; use a new stable scope key.");
    }

    private static async ValueTask EnsureExpansionNotRetiredAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid runId, string expansionId, CancellationToken ct)
    {
        await using var command = SqliteStoreSupport.CreateCommand(connection, transaction, """
            SELECT 1 FROM workflow_group_fanout_retirements WHERE workflow_run_id=$run AND expansion_id=$expansion;
            """);
        command.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(runId));
        command.Parameters.AddWithValue("$expansion", expansionId);
        if (await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null)
            throw new WorkflowStateException("The grouped fan-out selection has been explicitly retired.");
    }

    private static async ValueTask RetirePreviousAsync(SqliteConnection connection, SqliteTransaction transaction,
        ActivityGroupFanOutRequest request, BoundaryRow boundary, string replacementId, CancellationToken ct)
    {
        var plan = request.Plan;
        if (plan.Binding.ReplacesFanOutKey is not { } previousKey) return;
        string previousId;
        await using (var previous = SqliteStoreSupport.CreateCommand(connection, transaction, """
            SELECT expansion_id, boundary_step_key, boundary_revision, canonical_json
            FROM workflow_group_fanout_expansions WHERE workflow_run_id=$run AND fan_out_key=$key
            ORDER BY boundary_revision DESC LIMIT 1;
            """))
        {
            previous.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(plan.WorkflowRunId));
            previous.Parameters.AddWithValue("$key", previousKey);
            await using var reader = await previous.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                throw new WorkflowStateException("The scope explicitly selected for retirement does not exist.");
            previousId = reader.GetString(0);
            using var oldPlan = JsonDocument.Parse(reader.GetString(3));
            var old = oldPlan.RootElement;
            if (reader.GetString(1) != plan.Binding.SelectionStepKey || reader.GetInt32(2) >= boundary.Revision
                || old.GetProperty("group").GetString() != plan.Binding.Membership.GroupKey
                || old.GetProperty("parent").GetString() != plan.Binding.Parent?.GroupOccurrenceId)
                throw new WorkflowStateException("Replacement requires a newer revision of the same selection step and group root.");
        }
        await EnsureScopeNotRetiredAsync(connection, transaction, plan.WorkflowRunId, previousKey, ct).ConfigureAwait(false);
        // Reusing an existing replacement scope key could alias past execution.
        await using (var count = SqliteStoreSupport.CreateCommand(connection, transaction, """
            SELECT COUNT(*) FROM workflow_group_fanout_expansions WHERE workflow_run_id=$run AND fan_out_key=$key;
            """))
        {
            count.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(plan.WorkflowRunId));
            count.Parameters.AddWithValue("$key", plan.FanOutKey);
            if (Convert.ToInt64(await count.ExecuteScalarAsync(ct).ConfigureAwait(false)) != 1)
                throw new WorkflowStateException("Replacement requires a fresh stable scope key.");
        }
        await using (var insert = SqliteStoreSupport.CreateCommand(connection, transaction, """
            INSERT INTO workflow_group_fanout_retirements
                (workflow_run_id,expansion_id,replacement_expansion_id,lease_generation,retired_at)
            VALUES ($run,$old,$new,$generation,$now);
            """))
        {
            insert.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(plan.WorkflowRunId));
            insert.Parameters.AddWithValue("$old", previousId);
            insert.Parameters.AddWithValue("$new", replacementId);
            insert.Parameters.AddWithValue("$generation", request.LeaseGeneration);
            insert.Parameters.AddWithValue("$now", SqliteStoreSupport.FormatTimestamp(request.Now));
            await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        await using var retire = SqliteStoreSupport.CreateCommand(connection, transaction, """
            WITH RECURSIVE retired(id) AS (
                SELECT group_occurrence_id FROM workflow_group_fanout_items WHERE workflow_run_id=$run AND expansion_id=$old
                UNION
                SELECT o.group_occurrence_id FROM workflow_group_occurrences o JOIN retired r ON o.parent_group_occurrence_id=r.id
                WHERE o.workflow_run_id=$run
            )
            UPDATE workflow_steps SET status=$cancelled,completed_at=$now,lease_owner=NULL,lease_expires_at=NULL
            WHERE workflow_run_id=$run AND status IN ($pending,$running,$waiting)
              AND revision=(SELECT MAX(s.revision) FROM workflow_steps s
                  WHERE s.workflow_run_id=workflow_steps.workflow_run_id AND s.step_key=workflow_steps.step_key)
              AND id IN (SELECT step_id FROM workflow_step_group_memberships WHERE workflow_run_id=$run
                  AND group_occurrence_id IN (SELECT id FROM retired));
            """);
        retire.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(plan.WorkflowRunId));
        retire.Parameters.AddWithValue("$old", previousId);
        retire.Parameters.AddWithValue("$now", SqliteStoreSupport.FormatTimestamp(request.Now));
        retire.Parameters.AddWithValue("$cancelled", (int)StepStatus.Cancelled);
        retire.Parameters.AddWithValue("$pending", (int)StepStatus.Pending);
        retire.Parameters.AddWithValue("$running", (int)StepStatus.Running);
        retire.Parameters.AddWithValue("$waiting", (int)StepStatus.Waiting);
        await retire.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        await using (var waits = SqliteStoreSupport.CreateCommand(connection, transaction, """
            WITH RECURSIVE retired(id) AS (
                SELECT group_occurrence_id FROM workflow_group_fanout_items WHERE workflow_run_id=$run AND expansion_id=$old
                UNION
                SELECT o.group_occurrence_id FROM workflow_group_occurrences o JOIN retired r ON o.parent_group_occurrence_id=r.id
                WHERE o.workflow_run_id=$run
            )
            UPDATE workflow_waits SET status=$cancelled,updated_at=$now
            WHERE workflow_run_id=$run AND status IN ($parked,$ready)
              AND step_id IN (SELECT s.id FROM workflow_steps s JOIN workflow_step_group_memberships m ON m.step_id=s.id
                  WHERE s.workflow_run_id=$run AND s.status=$stepCancelled
                    AND m.group_occurrence_id IN (SELECT id FROM retired));
            """))
        {
            waits.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(plan.WorkflowRunId));
            waits.Parameters.AddWithValue("$old", previousId);
            waits.Parameters.AddWithValue("$now", SqliteStoreSupport.FormatTimestamp(request.Now));
            waits.Parameters.AddWithValue("$cancelled", (int)WaitStatus.Cancelled);
            waits.Parameters.AddWithValue("$stepCancelled", (int)StepStatus.Cancelled);
            waits.Parameters.AddWithValue("$parked", (int)WaitStatus.Parked);
            waits.Parameters.AddWithValue("$ready", (int)WaitStatus.Ready);
            await waits.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        await new InsertEventCommand().ExecuteAsync(connection, transaction, plan.WorkflowRunId,
            plan.Binding.SelectionStepKey, WorkflowEventTypes.GroupFanOutReplaced, request.Now, null,
            JsonSerializer.Serialize(new
            {
                previousExpansionId = previousId,
                replacementExpansionId = replacementId,
                leaseGeneration = request.LeaseGeneration
            }), ct).ConfigureAwait(false);
    }

    internal static async ValueTask<HashSet<string>> GetRetiredStepKeysAsync(SqliteConnection connection,
        SqliteTransaction transaction, Guid runId, CancellationToken ct)
    {
        await using var command = SqliteStoreSupport.CreateCommand(connection, transaction, """
            WITH RECURSIVE retired(id) AS (
                SELECT i.group_occurrence_id FROM workflow_group_fanout_items i JOIN workflow_group_fanout_retirements r
                  ON r.workflow_run_id=i.workflow_run_id AND r.expansion_id=i.expansion_id WHERE i.workflow_run_id=$run
                UNION
                SELECT o.group_occurrence_id FROM workflow_group_occurrences o JOIN retired r ON o.parent_group_occurrence_id=r.id
                WHERE o.workflow_run_id=$run
            )
            SELECT DISTINCT step_key FROM workflow_step_group_memberships WHERE workflow_run_id=$run
                AND group_occurrence_id IN (SELECT id FROM retired);
            """);
        command.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(runId));
        var keys = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) keys.Add(reader.GetString(0));
        return keys;
    }
}
