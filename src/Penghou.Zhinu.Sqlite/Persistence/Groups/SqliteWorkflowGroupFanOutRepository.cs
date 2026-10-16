using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Penghou.Zhinu.Sqlite.Persistence.Groups;

internal sealed class SqliteWorkflowGroupFanOutRepository(IZhinuSqliteDatabase factory)
{
    public async ValueTask<ActivityGroupFanOutState> AdmitAsync(
        ActivityGroupFanOutRequest request,
        CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        await factory.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var plan = request.Plan;
        var boundary = await ValidateFenceAndReadBoundaryAsync(
            connection, transaction, request, requireCompleted: true, cancellationToken).ConfigureAwait(false);
        var expansionId = plan.GetExpansionId(boundary.Id);
        await ValidateRunPinAndParentAsync(connection, transaction, plan, cancellationToken).ConfigureAwait(false);

        await using (var existing = SqliteStoreSupport.CreateCommand(connection, transaction, """
            SELECT plan_digest, canonical_json, item_count, boundary_revision
            FROM workflow_group_fanout_expansions
            WHERE workflow_run_id=$run AND expansion_id=$expansion;
            """))
        {
            existing.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(plan.WorkflowRunId));
            existing.Parameters.AddWithValue("$expansion", expansionId);
            await using var reader = await existing.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (reader.GetString(0) != plan.PlanDigest || reader.GetString(1) != plan.CanonicalJson ||
                    reader.GetInt32(2) != plan.Items.Count || reader.GetInt32(3) != boundary.Revision)
                    throw new WorkflowStateException("The durable fan-out boundary already has a different immutable selection.");
                await reader.DisposeAsync().ConfigureAwait(false);
                await EnsureMarkerAsync(connection, transaction, plan.WorkflowRunId, cancellationToken).ConfigureAwait(false);
                var state = await ReadStateAsync(connection, transaction, plan.WorkflowRunId, expansionId,
                    plan.FanOutKey, boundary.Id, boundary.Revision, plan.PlanDigest, plan.Items.Count, cancellationToken)
                    .ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return state;
            }
        }

        // A selection revision may be restarted, but this profile only permits
        // it to reproduce the same immutable fan-out contract.
        await using (var count = SqliteStoreSupport.CreateCommand(connection, transaction,
            "SELECT COUNT(*) FROM workflow_group_fanout_expansions WHERE workflow_run_id=$run;"))
        {
            count.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(plan.WorkflowRunId));
            if (Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) >= ActivityGroupOccurrence.MaximumAllowedOccurrencesPerRun)
                throw new WorkflowStateException("The run exceeds its grouped fan-out expansion limit.");
        }
        await using (var previous = SqliteStoreSupport.CreateCommand(connection, transaction, """
            SELECT plan_digest, canonical_json
            FROM workflow_group_fanout_expansions
            WHERE workflow_run_id=$run AND fan_out_key=$key
            ORDER BY boundary_revision DESC LIMIT 1;
            """))
        {
            previous.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(plan.WorkflowRunId));
            previous.Parameters.AddWithValue("$key", plan.FanOutKey);
            await using var reader = await previous.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false) &&
                (reader.GetString(0) != plan.PlanDigest || reader.GetString(1) != plan.CanonicalJson))
                throw new WorkflowStateException("A restarted selection boundary changed its grouped fan-out plan; this profile requires the same selection.");
        }

        foreach (var item in plan.Items)
            await EnsureStepKeyAvailableAsync(connection, transaction, plan.WorkflowRunId, item,
                cancellationToken).ConfigureAwait(false);
        var parents = new Stack<ActivityGroupOccurrence>();
        for (var parent = plan.Binding.Parent; parent is not null; parent = parent.Parent) parents.Push(parent);
        while (parents.TryPop(out var parent))
            await SqliteWorkflowGroupRepository.InsertOrCompareOccurrenceAsync(
                connection, transaction, parent, plan.GroupManifestDigest, cancellationToken).ConfigureAwait(false);
        // Root groups have no parent occurrence. Materialize every deterministic
        // item occurrence before any item claim can dispatch user code.
        foreach (var item in plan.Items)
            await SqliteWorkflowGroupRepository.InsertOrCompareOccurrenceAsync(
                connection, transaction, item.Group.Occurrence, plan.GroupManifestDigest, cancellationToken).ConfigureAwait(false);

        await using (var insert = SqliteStoreSupport.CreateCommand(connection, transaction, """
            INSERT INTO workflow_group_fanout_expansions
                (workflow_run_id, expansion_id, fan_out_key, boundary_step_id, boundary_step_key,
                 boundary_revision, plan_digest, canonical_json, item_count, admitted_generation)
            VALUES ($run, $expansion, $key, $boundary, $boundaryKey, $revision, $digest, $json, $count, $generation);
            """))
        {
            insert.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(plan.WorkflowRunId));
            insert.Parameters.AddWithValue("$expansion", expansionId);
            insert.Parameters.AddWithValue("$key", plan.FanOutKey);
            insert.Parameters.AddWithValue("$boundary", SqliteStoreSupport.Format(boundary.Id));
            insert.Parameters.AddWithValue("$boundaryKey", plan.Binding.SelectionStepKey);
            insert.Parameters.AddWithValue("$revision", boundary.Revision);
            insert.Parameters.AddWithValue("$digest", plan.PlanDigest);
            insert.Parameters.AddWithValue("$json", plan.CanonicalJson);
            insert.Parameters.AddWithValue("$count", plan.Items.Count);
            insert.Parameters.AddWithValue("$generation", request.LeaseGeneration);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        foreach (var item in plan.Items)
        {
            await using var insertItem = SqliteStoreSupport.CreateCommand(connection, transaction, """
                INSERT INTO workflow_group_fanout_items
                    (workflow_run_id, expansion_id, item_key, step_key, input_hash, group_occurrence_id, role)
                VALUES ($run, $expansion, $item, $step, $hash, $occurrence, $role);
                """);
            insertItem.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(plan.WorkflowRunId));
            insertItem.Parameters.AddWithValue("$expansion", expansionId);
            insertItem.Parameters.AddWithValue("$item", item.ItemKey);
            insertItem.Parameters.AddWithValue("$step", item.StepKey);
            insertItem.Parameters.AddWithValue("$hash", item.InputHash);
            insertItem.Parameters.AddWithValue("$occurrence", item.Group.Occurrence.GroupOccurrenceId);
            insertItem.Parameters.AddWithValue("$role", (int)item.Group.Membership.Role);
            await insertItem.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await EnsureMarkerAsync(connection, transaction, plan.WorkflowRunId, cancellationToken).ConfigureAwait(false);
        var admitted = await ReadStateAsync(connection, transaction, plan.WorkflowRunId, expansionId,
            plan.FanOutKey, boundary.Id, boundary.Revision, plan.PlanDigest, plan.Items.Count, cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return admitted;
    }

    public async ValueTask<ActivityGroupFanOutState> CompleteAsync(
        ActivityGroupFanOutRequest request,
        CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        await factory.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var plan = request.Plan;
        var boundary = await ValidateFenceAndReadBoundaryAsync(
            connection, transaction, request, requireCompleted: true, cancellationToken).ConfigureAwait(false);
        var expansionId = plan.GetExpansionId(boundary.Id);
        await EnsureExpansionMatchesAsync(connection, transaction, plan, boundary, expansionId, cancellationToken).ConfigureAwait(false);
        await ValidateRunPinAndParentAsync(connection, transaction, plan, cancellationToken).ConfigureAwait(false);
        var currentSteps = await ReadCompletedItemsAsync(connection, transaction, plan.WorkflowRunId, expansionId,
            plan.Items.Count, cancellationToken).ConfigureAwait(false);
        var currentJson = JsonSerializer.Serialize(currentSteps);
        var closureDigest = Digest("penghou.zhinu.activity-group-fanout-closure.v1\n" + currentJson);
        await using (var insert = SqliteStoreSupport.CreateCommand(connection, transaction, """
            INSERT INTO workflow_group_fanout_closures
                (workflow_run_id, expansion_id, current_steps_json, closure_digest, closure_generation, completed_at)
            VALUES ($run, $expansion, $steps, $digest, $generation, $at)
            ON CONFLICT(workflow_run_id, expansion_id, closure_digest) DO NOTHING;
            """))
        {
            insert.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(plan.WorkflowRunId));
            insert.Parameters.AddWithValue("$expansion", expansionId);
            insert.Parameters.AddWithValue("$steps", currentJson);
            insert.Parameters.AddWithValue("$digest", closureDigest);
            insert.Parameters.AddWithValue("$generation", request.LeaseGeneration);
            insert.Parameters.AddWithValue("$at", SqliteStoreSupport.FormatTimestamp(request.Now));
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        var state = await ReadStateAsync(connection, transaction, plan.WorkflowRunId, expansionId,
            plan.FanOutKey, boundary.Id, boundary.Revision, plan.PlanDigest, plan.Items.Count, cancellationToken)
            .ConfigureAwait(false);
        if (!state.IsComplete)
            throw new WorkflowStateException("A previous grouped fan-out closure proof does not match the current item revisions.");
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return state;
    }

    public async ValueTask<ActivityGroupFanOutState?> GetAsync(
        Guid workflowRunId, string fanOutKey, CancellationToken cancellationToken)
    {
        if (workflowRunId == Guid.Empty) throw new ArgumentException("Workflow ID must not be empty.", nameof(workflowRunId));
        ArgumentException.ThrowIfNullOrWhiteSpace(fanOutKey);
        await factory.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: true);
        await using var command = SqliteStoreSupport.CreateCommand(connection, transaction, """
            SELECT expansion_id, fan_out_key, boundary_step_id, boundary_revision, plan_digest, item_count
            FROM workflow_group_fanout_expansions
            WHERE workflow_run_id=$run AND fan_out_key=$key
            ORDER BY boundary_revision DESC LIMIT 1;
            """);
        command.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(workflowRunId));
        command.Parameters.AddWithValue("$key", fanOutKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        var state = new ExpansionRow(reader.GetString(0), reader.GetString(1), Guid.Parse(reader.GetString(2)),
            reader.GetInt32(3), reader.GetString(4), reader.GetInt32(5));
        await reader.DisposeAsync().ConfigureAwait(false);
        return await ReadStateAsync(connection, transaction, workflowRunId, state.ExpansionId, state.FanOutKey,
            state.BoundaryStepId, state.BoundaryRevision, state.PlanDigest, state.ItemCount, cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<bool> HasIncompleteAsync(Guid workflowRunId, CancellationToken cancellationToken)
    {
        if (workflowRunId == Guid.Empty) throw new ArgumentException("Workflow ID must not be empty.", nameof(workflowRunId));
        await factory.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: true);
        await using var command = SqliteStoreSupport.CreateCommand(connection, transaction, """
            SELECT expansion_id, fan_out_key, boundary_step_id, boundary_revision, plan_digest, item_count
            FROM workflow_group_fanout_expansions e
            WHERE workflow_run_id=$run AND boundary_revision=(
                SELECT MAX(x.boundary_revision) FROM workflow_group_fanout_expansions x
                WHERE x.workflow_run_id=e.workflow_run_id AND x.fan_out_key=e.fan_out_key)
            ORDER BY fan_out_key;
            """);
        command.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(workflowRunId));
        var rows = new List<ExpansionRow>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                rows.Add(new ExpansionRow(reader.GetString(0), reader.GetString(1), Guid.Parse(reader.GetString(2)),
                    reader.GetInt32(3), reader.GetString(4), reader.GetInt32(5)));
        foreach (var row in rows)
        {
            var state = await ReadStateAsync(connection, transaction, workflowRunId, row.ExpansionId, row.FanOutKey,
                row.BoundaryStepId, row.BoundaryRevision, row.PlanDigest, row.ItemCount, cancellationToken)
                .ConfigureAwait(false);
            if (!state.IsComplete) return true;
        }
        return false;
    }

    internal static async ValueTask ValidateItemClaimAsync(
        SqliteConnection connection, SqliteTransaction transaction, StepClaimRequest request,
        CancellationToken cancellationToken)
    {
        var expected = await FindReservedItemAsync(connection, transaction, request.WorkflowRunId,
            request.StepKey, cancellationToken).ConfigureAwait(false);
        var claim = request.FanOut;
        if (expected is null)
        {
            if (claim is not null) throw new WorkflowStateException("The grouped fan-out item claim is not in an admitted enumeration.");
            return;
        }
        if (claim is null || claim.ExpansionId != expected.ExpansionId || claim.ItemKey != expected.ItemKey)
            throw new WorkflowStateException("A selected grouped fan-out item must claim its admitted expansion and item key.");
        if (request.InputHash != expected.InputHash || request.Group?.Occurrence.GroupOccurrenceId != expected.OccurrenceId ||
            (int?)request.Group?.Membership.Role != expected.Role)
            throw new WorkflowStateException("The grouped fan-out item claim does not match its admitted input or occurrence.");
        await using var boundary = SqliteStoreSupport.CreateCommand(connection, transaction, """
            SELECT e.canonical_json, b.status, e.boundary_revision,
                   (SELECT MAX(s.revision) FROM workflow_steps s
                    WHERE s.workflow_run_id=e.workflow_run_id AND s.step_key=e.boundary_step_key)
            FROM workflow_group_fanout_expansions e
            JOIN workflow_steps b ON b.id=e.boundary_step_id AND b.workflow_run_id=e.workflow_run_id
                AND b.step_key=e.boundary_step_key AND b.revision=e.boundary_revision
            WHERE e.workflow_run_id=$run AND e.expansion_id=$expansion;
            """);
        boundary.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(request.WorkflowRunId));
        boundary.Parameters.AddWithValue("$expansion", claim.ExpansionId);
        await using var reader = await boundary.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ||
            (StepStatus)reader.GetInt32(1) != StepStatus.Completed || reader.GetInt32(3) != reader.GetInt32(2))
            throw new WorkflowStateException("The grouped fan-out selection boundary is no longer current and complete.");
        using var json = JsonDocument.Parse(reader.GetString(0));
        ValidateClaimContract(json.RootElement, request);
    }

    private static async ValueTask<BoundaryRow> ValidateFenceAndReadBoundaryAsync(
        SqliteConnection connection, SqliteTransaction transaction, ActivityGroupFanOutRequest request,
        bool requireCompleted, CancellationToken cancellationToken)
    {
        var plan = request.Plan;
        await using var fence = SqliteStoreSupport.CreateCommand(connection, transaction, """
            SELECT r.status, r.lease_generation, r.lease_owner, r.lease_expires_at,
                   (SELECT g.status FROM workflow_generations g WHERE g.workflow_run_id=r.id
                    ORDER BY CASE WHEN g.status IN (2,5) THEN 0 ELSE 1 END, g.ordinal DESC LIMIT 1)
            FROM workflow_runs r
            WHERE r.id=$run;
            """);
        fence.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(plan.WorkflowRunId));
        await using (var reader = await fence.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw new WorkflowNotFoundException($"Workflow '{plan.WorkflowRunId:D}' does not exist.");
            if ((WorkflowStatus)reader.GetInt32(0) is not (WorkflowStatus.Pending or WorkflowStatus.Running) ||
                reader.GetInt64(1) != request.LeaseGeneration ||
                !string.Equals(SqliteStoreSupport.GetNullableString(reader, 2), request.OwnerId, StringComparison.Ordinal) ||
                SqliteStoreSupport.ParseNullableTimestamp(reader, 3) is not { } expiry || expiry <= request.Now ||
                (!reader.IsDBNull(4) && reader.GetInt32(4) is not ((int)WorkflowGenerationStatus.Active) and not ((int)WorkflowGenerationStatus.Created) and not ((int)WorkflowGenerationStatus.Prepared)))
                throw new LeaseLostException("The grouped fan-out operation lost its run lease or generation fence.");
        }
        var key = plan.Binding.SelectionStepKey;
        await using var select = SqliteStoreSupport.CreateCommand(connection, transaction, """
            SELECT s.id,s.revision,s.status,s.input_type,s.output_type,s.implementation_key,
                   s.authorization_declaration_hash,
                   EXISTS(SELECT 1 FROM workflow_step_group_memberships m WHERE m.step_id=s.id)
            FROM workflow_steps s
            WHERE s.workflow_run_id=$run AND s.step_key=$key
              AND s.revision=(SELECT MAX(x.revision) FROM workflow_steps x
                  WHERE x.workflow_run_id=s.workflow_run_id AND x.step_key=s.step_key);
            """);
        select.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(plan.WorkflowRunId));
        select.Parameters.AddWithValue("$key", key);
        await using var selected = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await selected.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new WorkflowStateException("The grouped fan-out selection step does not exist.");
        var boundary = new BoundaryRow(Guid.Parse(selected.GetString(0)), selected.GetInt32(1),
            (StepStatus)selected.GetInt32(2), SqliteStoreSupport.GetNullableString(selected, 3),
            selected.GetString(4), SqliteStoreSupport.GetNullableString(selected, 5),
            SqliteStoreSupport.GetNullableString(selected, 6), selected.GetBoolean(7));
        if (requireCompleted && boundary.Status != StepStatus.Completed)
            throw new WorkflowStateException("Grouped fan-out requires its existing selection step to be completed.");
        return boundary;
    }

    private static async ValueTask ValidateRunPinAndParentAsync(
        SqliteConnection connection, SqliteTransaction transaction, ActivityGroupFanOutPlan plan,
        CancellationToken cancellationToken)
    {
        await using var command = SqliteStoreSupport.CreateCommand(connection, transaction, """
            SELECT r.group_manifest_digest, m.manifest_digest, r.group_fanout_used
            FROM workflow_runs r LEFT JOIN workflow_group_manifests m ON m.workflow_run_id=r.id
            WHERE r.id=$run;
            """);
        command.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(plan.WorkflowRunId));
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ||
                reader.IsDBNull(0) || reader.IsDBNull(1) || reader.GetString(0) != plan.GroupManifestDigest ||
                reader.GetString(1) != plan.GroupManifestDigest)
                throw new WorkflowStateException("The grouped fan-out plan does not match the run's durable manifest pin.");
        }
    }

    private static async ValueTask EnsureExpansionMatchesAsync(
        SqliteConnection connection, SqliteTransaction transaction, ActivityGroupFanOutPlan plan,
        BoundaryRow boundary, string expansionId, CancellationToken cancellationToken)
    {
        await using var command = SqliteStoreSupport.CreateCommand(connection, transaction, """
            SELECT plan_digest, canonical_json, item_count, boundary_revision
            FROM workflow_group_fanout_expansions WHERE workflow_run_id=$run AND expansion_id=$expansion;
            """);
        command.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(plan.WorkflowRunId));
        command.Parameters.AddWithValue("$expansion", expansionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || reader.GetString(0) != plan.PlanDigest ||
            reader.GetString(1) != plan.CanonicalJson || reader.GetInt32(2) != plan.Items.Count ||
            reader.GetInt32(3) != boundary.Revision)
            throw new WorkflowStateException("The grouped fan-out expansion does not match its admitted selection.");
    }

    private static async ValueTask<IReadOnlyList<string>> ReadCompletedItemsAsync(
        SqliteConnection connection, SqliteTransaction? transaction, Guid runId, string expansionId,
        int expectedCount, CancellationToken cancellationToken)
    {
        await using var command = SqliteStoreSupport.CreateCommand(connection, transaction, """
            SELECT i.item_key,i.step_key,i.input_hash,i.group_occurrence_id,i.role,
                   s.id,s.revision,s.status,m.group_occurrence_id,m.role,s.input_hash
            FROM workflow_group_fanout_items i
            LEFT JOIN workflow_steps s ON s.workflow_run_id=i.workflow_run_id AND s.step_key=i.step_key
                AND s.revision=(SELECT MAX(x.revision) FROM workflow_steps x
                    WHERE x.workflow_run_id=i.workflow_run_id AND x.step_key=i.step_key)
            LEFT JOIN workflow_step_group_memberships m ON m.workflow_run_id=s.workflow_run_id AND m.step_id=s.id
            WHERE i.workflow_run_id=$run AND i.expansion_id=$expansion ORDER BY i.item_key;
            """);
        command.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(runId));
        command.Parameters.AddWithValue("$expansion", expansionId);
        var snapshots = new List<string>(expectedCount);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (reader.IsDBNull(5) || reader.GetInt32(7) != (int)StepStatus.Completed ||
                reader.GetString(2) != reader.GetString(10) ||
                reader.IsDBNull(8) || reader.GetString(3) != reader.GetString(8) ||
                reader.GetInt32(4) != reader.GetInt32(9))
                throw new WorkflowStateException($"Grouped fan-out item '{reader.GetString(0)}' is not completed at its admitted input and occurrence.");
            snapshots.Add(JsonSerializer.Serialize(new { key = reader.GetString(0), step = reader.GetString(5), revision = reader.GetInt32(6) }));
        }
        if (snapshots.Count != expectedCount)
            throw new WorkflowStateException("The grouped fan-out enumeration is incomplete.");
        return snapshots;
    }

    private static async ValueTask<ActivityGroupFanOutState> ReadStateAsync(
        SqliteConnection connection, SqliteTransaction? transaction, Guid runId, string expansionId,
        string fanOutKey, Guid boundaryStepId, int boundaryRevision, string planDigest, int itemCount,
        CancellationToken cancellationToken)
    {
        var complete = false;
        IReadOnlyList<string>? current = null;
        try
        {
            current = await ReadCompletedItemsAsync(connection, transaction, runId, expansionId,
                itemCount, cancellationToken).ConfigureAwait(false);
        }
        catch (WorkflowStateException) { }
        if (current is not null)
        {
            var json = JsonSerializer.Serialize(current);
            var digest = Digest("penghou.zhinu.activity-group-fanout-closure.v1\n" + json);
            await using var command = SqliteStoreSupport.CreateCommand(connection, transaction, """
                SELECT c.current_steps_json,
                       (SELECT MAX(s.revision) FROM workflow_steps s
                        WHERE s.workflow_run_id=e.workflow_run_id AND s.step_key=e.boundary_step_key)
                FROM workflow_group_fanout_expansions e
                JOIN workflow_group_fanout_closures c ON c.workflow_run_id=e.workflow_run_id AND c.expansion_id=e.expansion_id
                WHERE e.workflow_run_id=$run AND e.expansion_id=$expansion AND c.closure_digest=$digest;
                """);
            command.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(runId));
            command.Parameters.AddWithValue("$expansion", expansionId);
            command.Parameters.AddWithValue("$digest", digest);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            complete = await reader.ReadAsync(cancellationToken).ConfigureAwait(false) &&
                reader.GetString(0) == json && reader.GetInt32(1) == boundaryRevision;
        }
        return new ActivityGroupFanOutState
        {
            WorkflowRunId = runId,
            ExpansionId = expansionId,
            FanOutKey = fanOutKey,
            BoundaryStepId = boundaryStepId,
            BoundaryRevision = boundaryRevision,
            PlanDigest = planDigest,
            ItemCount = itemCount,
            IsComplete = complete
        };
    }
    private static async ValueTask EnsureMarkerAsync(
        SqliteConnection connection, SqliteTransaction transaction, Guid runId, CancellationToken cancellationToken)
    {
        await using var command = SqliteStoreSupport.CreateCommand(connection, transaction,
            "UPDATE workflow_runs SET group_fanout_used=1 WHERE id=$run;");
        command.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(runId));
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new WorkflowNotFoundException($"Workflow '{runId:D}' does not exist.");
    }

    private static async ValueTask<ReservedItem?> FindReservedItemAsync(
        SqliteConnection connection, SqliteTransaction transaction, Guid runId, string stepKey,
        CancellationToken cancellationToken)
    {
        await using var command = SqliteStoreSupport.CreateCommand(connection, transaction, """
            SELECT i.expansion_id,i.item_key,i.input_hash,i.group_occurrence_id,i.role
            FROM workflow_group_fanout_items i
            JOIN workflow_group_fanout_expansions e ON e.workflow_run_id=i.workflow_run_id AND e.expansion_id=i.expansion_id
            WHERE i.workflow_run_id=$run AND i.step_key=$step
            ORDER BY e.boundary_revision DESC LIMIT 1;
            """);
        // Role is fixed by the immutable plan; the item occurrence is validated
        // against the supplied role through the group membership request below.
        command.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(runId));
        command.Parameters.AddWithValue("$step", stepKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        return new ReservedItem(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt32(4));
    }

    private static async ValueTask EnsureStepKeyAvailableAsync(
        SqliteConnection connection, SqliteTransaction transaction, Guid runId,
        ActivityGroupFanOutItem item, CancellationToken cancellationToken)
    {
        await using (var reserved = SqliteStoreSupport.CreateCommand(connection, transaction, """
            SELECT 1 FROM workflow_group_fanout_items
            WHERE workflow_run_id=$run AND step_key=$key AND group_occurrence_id<>$occurrence LIMIT 1;
            """))
        {
            reserved.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(runId));
            reserved.Parameters.AddWithValue("$key", item.StepKey);
            reserved.Parameters.AddWithValue("$occurrence", item.Group.Occurrence.GroupOccurrenceId);
            if (await reserved.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null)
                throw new WorkflowStateException("A different fan-out already selected the same native step key.");
        }
        await using var command = SqliteStoreSupport.CreateCommand(connection, transaction, """
            SELECT s.id,m.group_occurrence_id
            FROM workflow_steps s
            LEFT JOIN workflow_step_group_memberships m ON m.workflow_run_id=s.workflow_run_id AND m.step_id=s.id
            WHERE s.workflow_run_id=$run AND s.step_key=$key
              AND s.revision=(SELECT MAX(x.revision) FROM workflow_steps x
                  WHERE x.workflow_run_id=s.workflow_run_id AND x.step_key=s.step_key);
            """);
        command.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(runId));
        command.Parameters.AddWithValue("$key", item.StepKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false) &&
            (reader.IsDBNull(1) || reader.GetString(1) != item.Group.Occurrence.GroupOccurrenceId))
            throw new WorkflowStateException($"Grouped fan-out item step key '{item.StepKey}' collides with an existing durable step.");
    }

    private static void ValidateClaimContract(JsonElement plan, StepClaimRequest request)
    {
        if (plan.GetProperty("inputType").GetString() != request.InputType ||
            plan.GetProperty("outputType").GetString() != request.OutputType ||
            plan.GetProperty("implementation").GetString() != request.ImplementationKey ||
            plan.GetProperty("authorization").GetString() != request.AuthorizationDeclarationHash ||
            plan.GetProperty("compensation").GetString() != ActivityGroupFanOutPlan.CompensationSignature(request.Compensation))
            throw new WorkflowStateException("The grouped fan-out item claim changed its admitted execution contract.");
        var expected = plan.GetProperty("dependencies").EnumerateArray().Select(item => item.GetString()!)
            .Order(StringComparer.Ordinal).ToArray();
        var actual = (request.DependsOn ?? []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (!expected.SequenceEqual(actual, StringComparer.Ordinal))
            throw new WorkflowStateException("The grouped fan-out item claim changed its admitted dependencies.");
    }

    private static void ValidateRequest(ActivityGroupFanOutRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OwnerId);
        if (request.LeaseGeneration < 1) throw new ArgumentOutOfRangeException(nameof(request.LeaseGeneration));
    }

    private static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed record BoundaryRow(Guid Id, int Revision, StepStatus Status, string? InputType,
        string OutputType, string? ImplementationKey, string? AuthorizationHash, bool IsGrouped);
    private sealed record ExpansionRow(string ExpansionId, string FanOutKey, Guid BoundaryStepId,
        int BoundaryRevision, string PlanDigest, int ItemCount);
    private sealed record ReservedItem(string ExpansionId, string ItemKey, string InputHash,
        string OccurrenceId, int Role);
}
