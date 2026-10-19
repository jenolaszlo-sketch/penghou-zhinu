using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Penghou.Zhinu.Sqlite.Persistence.Groups;

internal sealed class SqliteWorkflowGroupFanOutRepository(IZhinuSqliteDatabase factory)
{
    private const string ClosureDomain = "penghou.zhinu.activity-group-fanout-closure.v1\n";
    private const string BodyClosureDomain = "penghou.zhinu.activity-group-fanout-closure.v2\n";
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
        var currentJson = JsonSerializer.Serialize(currentSteps.Entries);
        var closureDigest = Digest((currentSteps.IsBody ? BodyClosureDomain : ClosureDomain) + currentJson);
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
        if (expected?.BodyContract is not null)
        {
            await ValidateBodyItemClaimAsync(connection, transaction, request, expected, cancellationToken)
                .ConfigureAwait(false);
            return;
        }
        if (expected is null)
        {
            if (claim is not null && (claim.BodyOperationKey is not null || claim.IsBodyCompletion))
            {
                var bodyItem = await FindBodyItemAsync(connection, transaction, request.WorkflowRunId,
                    claim.ExpansionId, claim.ItemKey, cancellationToken).ConfigureAwait(false)
                    ?? throw new WorkflowStateException("The grouped fan-out body claim is not in an admitted body enumeration.");
                await ValidateBodyItemClaimAsync(connection, transaction, request, bodyItem, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }
            if (claim is not null) throw new WorkflowStateException("The grouped fan-out item claim is not in an admitted enumeration.");
            if (await IsBodyOperationNamespaceKeyAsync(connection, transaction, request.WorkflowRunId,
                    request.StepKey, cancellationToken).ConfigureAwait(false))
                throw new WorkflowStateException("A grouped fan-out body namespace requires its typed item scope claim.");
            if (request.Group is not null && await IsBodyOccurrenceAsync(connection, transaction, request,
                    cancellationToken).ConfigureAwait(false))
                throw new WorkflowStateException("A grouped fan-out body occurrence requires its admitted item claim.");
            return;
        }
        if (claim is not null && (claim.BodyOperationKey is not null || claim.IsBodyCompletion))
            throw new WorkflowStateException("A legacy grouped fan-out item cannot be reinterpreted as a body-mode claim.");
        if (expected.BodyContract is not null)
            throw new WorkflowStateException("The grouped fan-out completion claim changed its body-mode contract.");
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

    internal static async ValueTask<bool> IsBodyModeStepAsync(
        SqliteConnection connection, SqliteTransaction transaction, Guid workflowRunId, string stepKey,
        CancellationToken cancellationToken)
    {
        const string separator = ":body:";
        var separatorAt = stepKey.LastIndexOf(separator, StringComparison.Ordinal);
        if (separatorAt >= 0)
        {
            var key = stepKey[..separatorAt];
            var tail = stepKey[(separatorAt + separator.Length)..];
            var itemSeparator = tail.IndexOf(':');
            if (itemSeparator <= 0 || !TryDecodeSegment(tail[..itemSeparator], out var itemKey)) return false;
            await using var bodyCommand = SqliteStoreSupport.CreateCommand(connection, transaction, """
                SELECT e.canonical_json FROM workflow_group_fanout_expansions e
                JOIN workflow_group_fanout_items i
                  ON i.workflow_run_id=e.workflow_run_id AND i.expansion_id=e.expansion_id
                WHERE e.workflow_run_id=$run AND e.fan_out_key=$key AND i.item_key=$item
                ORDER BY e.boundary_revision DESC LIMIT 1;
                """);
            bodyCommand.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(workflowRunId));
            bodyCommand.Parameters.AddWithValue("$key", key);
            bodyCommand.Parameters.AddWithValue("$item", itemKey);
            var canonical = await bodyCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
            return canonical is not null && ReadBodyContract(canonical) is not null;
        }

        await using var completionCommand = SqliteStoreSupport.CreateCommand(connection, transaction, """
            SELECT e.canonical_json FROM workflow_group_fanout_items i
            JOIN workflow_group_fanout_expansions e
              ON e.workflow_run_id=i.workflow_run_id AND e.expansion_id=i.expansion_id
            WHERE i.workflow_run_id=$run AND i.step_key=$step
            ORDER BY e.boundary_revision DESC LIMIT 1;
            """);
        completionCommand.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(workflowRunId));
        completionCommand.Parameters.AddWithValue("$step", stepKey);
        var completionPlan = await completionCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        return completionPlan is not null && ReadBodyContract(completionPlan) is not null;
    }

    private static string EncodeSegment(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool TryDecodeSegment(string value, out string decoded)
    {
        try
        {
            var base64 = value.Replace('-', '+').Replace('_', '/');
            base64 += new string('=', (4 - base64.Length % 4) % 4);
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(base64));
            return EncodeSegment(decoded) == value;
        }
        catch (FormatException)
        {
            decoded = "";
            return false;
        }
    }

    private static async ValueTask<bool> IsBodyOperationNamespaceKeyAsync(
        SqliteConnection connection, SqliteTransaction transaction, Guid workflowRunId, string stepKey,
        CancellationToken cancellationToken)
    {
        const string separator = ":body:";
        var separatorAt = stepKey.LastIndexOf(separator, StringComparison.Ordinal);
        if (separatorAt < 0) return false;
        var tail = stepKey[(separatorAt + separator.Length)..];
        var itemSeparator = tail.IndexOf(':');
        if (itemSeparator <= 0 || !TryDecodeSegment(tail[..itemSeparator], out var itemKey)) return false;
        await using var command = SqliteStoreSupport.CreateCommand(connection, transaction, """
            SELECT e.canonical_json FROM workflow_group_fanout_expansions e
            JOIN workflow_group_fanout_items i
              ON i.workflow_run_id=e.workflow_run_id AND i.expansion_id=e.expansion_id
            WHERE e.workflow_run_id=$run AND e.fan_out_key=$key AND i.item_key=$item
            ORDER BY e.boundary_revision DESC LIMIT 1;
            """);
        command.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(workflowRunId));
        command.Parameters.AddWithValue("$key", stepKey[..separatorAt]);
        command.Parameters.AddWithValue("$item", itemKey);
        var canonical = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        return canonical is not null && ReadBodyContract(canonical) is not null;
    }

    private static async ValueTask ValidateBodyItemClaimAsync(
        SqliteConnection connection, SqliteTransaction transaction, StepClaimRequest request,
        ReservedItem expected, CancellationToken cancellationToken)
    {
        var claim = request.FanOut;
        if (claim is null || claim.ExpansionId != expected.ExpansionId || claim.ItemKey != expected.ItemKey)
            throw new WorkflowStateException("A body-mode fan-out claim must identify its exact admitted item.");
        await ValidateBodyBoundaryAsync(connection, transaction, request.WorkflowRunId, expected.ExpansionId,
            cancellationToken).ConfigureAwait(false);
        using var plan = JsonDocument.Parse(expected.CanonicalJson);
        var root = plan.RootElement;
        if (root.GetProperty("bodyContract").GetString() != expected.BodyContract)
            throw new WorkflowStateException("The admitted fan-out body contract is malformed.");
        if (claim.IsBodyCompletion)
        {
            if (claim.BodyOperationKey is not null || request.StepKey != expected.StepKey ||
                request.InputHash != expected.InputHash || request.Group is null ||
                request.Group.Occurrence.GroupOccurrenceId != expected.OccurrenceId ||
                request.Group.Membership.Role != ActivityGroupMemberRole.Control)
                throw new WorkflowStateException("The grouped fan-out body completion does not match its reserved item claim.");
            if (request.InputType != root.GetProperty("inputType").GetString() ||
                request.OutputType != root.GetProperty("outputType").GetString() ||
                request.ImplementationKey != root.GetProperty("implementation").GetString() ||
                request.AuthorizationDeclarationHash != root.GetProperty("authorization").GetString() ||
                request.Compensation is not null)
                throw new WorkflowStateException("The grouped fan-out body completion changed its admitted execution contract.");
            var required = root.GetProperty("dependencies").EnumerateArray().Select(item => item.GetString()!)
                .ToHashSet(StringComparer.Ordinal);
            var actual = (request.DependsOn ?? []).ToHashSet(StringComparer.Ordinal);
            if (!required.IsSubsetOf(actual) || actual.Contains(request.StepKey))
                throw new WorkflowStateException("A grouped fan-out body completion omitted its admitted selection or item operations.");
            return;
        }

        if (claim.BodyOperationKey is null || request.StepKey != claim.BodyOperationKey ||
            request.StepKey == expected.StepKey || request.Group is null ||
            !IsOccurrenceDescendant(request.Group.Occurrence, expected.OccurrenceId) ||
            request.Group.Membership.Role != ActivityGroupMemberRole.Work)
            throw new WorkflowStateException("A grouped fan-out body operation is outside its admitted item scope.");
        var prefix = root.GetProperty("fanOut").GetString() + ":body:";
        if (!request.StepKey.StartsWith(prefix, StringComparison.Ordinal))
            throw new WorkflowStateException("A grouped fan-out body operation key is outside its reserved namespace.");
        var requiredBodyDependencies = root.GetProperty("dependencies").EnumerateArray().Select(item => item.GetString()!)
            .ToHashSet(StringComparer.Ordinal);
        var actualBodyDependencies = (request.DependsOn ?? []).ToHashSet(StringComparer.Ordinal);
        if (!requiredBodyDependencies.IsSubsetOf(actualBodyDependencies))
            throw new WorkflowStateException("A grouped fan-out body operation omitted an admitted selection dependency.");
        if (request.DependsOn?.Any(dependency => dependency == request.StepKey) == true)
            throw new WorkflowStateException("A grouped fan-out body operation cannot depend on itself.");
    }

    private static async ValueTask ValidateBodyBoundaryAsync(
        SqliteConnection connection, SqliteTransaction transaction, Guid runId, string expansionId,
        CancellationToken cancellationToken)
    {
        await using var command = SqliteStoreSupport.CreateCommand(connection, transaction, """
            SELECT e.boundary_step_key,e.boundary_step_id,e.boundary_revision,b.status,
                   (SELECT MAX(s.revision) FROM workflow_steps s
                    WHERE s.workflow_run_id=e.workflow_run_id AND s.step_key=e.boundary_step_key)
            FROM workflow_group_fanout_expansions e
            JOIN workflow_steps b ON b.workflow_run_id=e.workflow_run_id AND b.id=e.boundary_step_id
                AND b.step_key=e.boundary_step_key AND b.revision=e.boundary_revision
            WHERE e.workflow_run_id=$run AND e.expansion_id=$expansion;
            """);
        command.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(runId));
        command.Parameters.AddWithValue("$expansion", expansionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ||
            (StepStatus)reader.GetInt32(3) != StepStatus.Completed || reader.GetInt32(4) != reader.GetInt32(2))
            throw new WorkflowStateException("The grouped fan-out body selection boundary is no longer current and complete.");
    }

    private static async ValueTask<bool> IsBodyOccurrenceAsync(
        SqliteConnection connection, SqliteTransaction transaction, StepClaimRequest request,
        CancellationToken cancellationToken)
    {
        var occurrence = request.Group!.Occurrence;
        await using var command = SqliteStoreSupport.CreateCommand(connection, transaction, """
            SELECT 1 FROM workflow_group_fanout_items i
            JOIN workflow_group_fanout_expansions e
              ON e.workflow_run_id=i.workflow_run_id AND e.expansion_id=i.expansion_id
            WHERE i.workflow_run_id=$run AND json_extract(e.canonical_json,'$.bodyContract') IS NOT NULL
              AND i.group_occurrence_id=$occurrence LIMIT 1;
            """);
        command.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(request.WorkflowRunId));
        command.Parameters.AddWithValue("$occurrence", occurrence.GroupOccurrenceId);
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null) return true;
        for (var parent = occurrence.Parent; parent is not null; parent = parent.Parent)
        {
            await using var parentCheck = SqliteStoreSupport.CreateCommand(connection, transaction, """
                SELECT 1 FROM workflow_group_fanout_items i
                JOIN workflow_group_fanout_expansions e
                  ON e.workflow_run_id=i.workflow_run_id AND e.expansion_id=i.expansion_id
                WHERE i.workflow_run_id=$run AND json_extract(e.canonical_json,'$.bodyContract') IS NOT NULL
                  AND i.group_occurrence_id=$occurrence LIMIT 1;
                """);
            parentCheck.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(request.WorkflowRunId));
            parentCheck.Parameters.AddWithValue("$occurrence", parent.GroupOccurrenceId);
            if (await parentCheck.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null) return true;
        }
        return false;
    }

    private static bool IsOccurrenceDescendant(ActivityGroupOccurrence occurrence, string rootOccurrenceId)
    {
        for (ActivityGroupOccurrence? current = occurrence; current is not null; current = current.Parent)
            if (current.GroupOccurrenceId == rootOccurrenceId) return true;
        return false;
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

    private static async ValueTask<ClosureVector> ReadCompletedItemsAsync(
        SqliteConnection connection, SqliteTransaction? transaction, Guid runId, string expansionId,
        int expectedCount, CancellationToken cancellationToken)
    {
        string canonicalPlan;
        string boundaryStepKey;
        await using (var planCommand = SqliteStoreSupport.CreateCommand(connection, transaction, """
            SELECT canonical_json, boundary_step_key FROM workflow_group_fanout_expansions
            WHERE workflow_run_id=$run AND expansion_id=$expansion;
            """))
        {
            planCommand.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(runId));
            planCommand.Parameters.AddWithValue("$expansion", expansionId);
            await using var planReader = await planCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await planReader.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw new WorkflowStateException("The grouped fan-out expansion is missing its immutable plan.");
            canonicalPlan = planReader.GetString(0);
            boundaryStepKey = planReader.GetString(1);
        }
        using var plan = JsonDocument.Parse(canonicalPlan);
        var bodyMode = ReadBodyContract(canonicalPlan) is not null;
        var requiredDependencies = plan.RootElement.GetProperty("dependencies").EnumerateArray()
            .Select(item => item.GetString()!).ToArray();
        await using var command = SqliteStoreSupport.CreateCommand(connection, transaction, """
            SELECT i.item_key,i.step_key,i.input_hash,i.group_occurrence_id,i.role,
                   s.id,s.revision,s.status,m.group_occurrence_id,m.role,s.input_hash,
                   e.boundary_step_key,e.canonical_json
            FROM workflow_group_fanout_items i
            JOIN workflow_group_fanout_expansions e ON e.workflow_run_id=i.workflow_run_id AND e.expansion_id=i.expansion_id
            LEFT JOIN workflow_steps s ON s.workflow_run_id=i.workflow_run_id AND s.step_key=i.step_key
                AND s.revision=(SELECT MAX(x.revision) FROM workflow_steps x
                    WHERE x.workflow_run_id=i.workflow_run_id AND x.step_key=i.step_key)
            LEFT JOIN workflow_step_group_memberships m ON m.workflow_run_id=s.workflow_run_id AND m.step_id=s.id
            WHERE i.workflow_run_id=$run AND i.expansion_id=$expansion ORDER BY i.item_key;
            """);
        command.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(runId));
        command.Parameters.AddWithValue("$expansion", expansionId);
        var snapshots = new List<string>(expectedCount);
        var itemRows = 0;
        var completedItems = new List<CompletedItemRow>(expectedCount);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            itemRows++;
            if (reader.GetString(11) != boundaryStepKey || bodyMode != (ReadBodyContract(reader.GetString(12)) is not null))
                throw new WorkflowStateException("A fan-out expansion mixes body and single-step item contracts.");
            if (reader.IsDBNull(5) || reader.GetInt32(7) != (int)StepStatus.Completed ||
                reader.GetString(2) != reader.GetString(10) ||
                reader.IsDBNull(8) || reader.GetString(3) != reader.GetString(8) ||
                reader.GetInt32(4) != reader.GetInt32(9) ||
                (bodyMode == true && reader.GetInt32(9) != (int)ActivityGroupMemberRole.Control))
                throw new WorkflowStateException($"Grouped fan-out item '{reader.GetString(0)}' is not completed at its admitted input and occurrence.");
            completedItems.Add(new CompletedItemRow(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetInt32(4), reader.GetString(5), reader.GetInt32(6),
                reader.GetString(8), reader.GetInt32(9), reader.GetString(10), reader.GetString(11)));
        }
        await reader.DisposeAsync().ConfigureAwait(false);
        if (itemRows != expectedCount) throw new WorkflowStateException("The grouped fan-out enumeration is incomplete.");
        IEnumerable<CompletedItemRow> orderedItems = bodyMode
            ? completedItems.OrderBy(item => item.ItemKey, StringComparer.Ordinal)
            : completedItems;
        foreach (var item in orderedItems)
        {
            if (bodyMode != true)
            {
                snapshots.Add(JsonSerializer.Serialize(new { key = item.ItemKey, step = item.StepId, revision = item.Revision }));
                continue;
            }
            snapshots.Add(SerializeBodyRevision(item.StepKey, item.StepId, item.Revision,
                item.CurrentInputHash, item.MembershipOccurrenceId, item.MembershipRole));
            var body = await ReadBodyOperationRevisionsAsync(connection, transaction, runId, item.StepKey,
                item.ItemOccurrenceId, boundaryStepKey, requiredDependencies, cancellationToken).ConfigureAwait(false);
            snapshots.AddRange(body);
        }
        return new ClosureVector(bodyMode, snapshots.AsReadOnly());
    }

    private static async ValueTask<IReadOnlyList<string>> ReadBodyOperationRevisionsAsync(
        SqliteConnection connection, SqliteTransaction? transaction, Guid runId, string itemStepKey,
        string itemOccurrenceId, string boundaryStepKey, IReadOnlyCollection<string> planDependencies,
        CancellationToken cancellationToken)
    {
        var dependencies = new HashSet<string>(planDependencies, StringComparer.Ordinal) { boundaryStepKey };
        var rows = new List<BodyDependencyRow>();
        await using (var command = SqliteStoreSupport.CreateCommand(connection, transaction, """
            SELECT d.depends_on_step_key,s.id,s.revision,s.status,s.input_hash,m.group_occurrence_id,m.role
            FROM workflow_step_dependencies d
            LEFT JOIN workflow_steps s ON s.workflow_run_id=d.run_id AND s.step_key=d.depends_on_step_key
              AND s.revision=(SELECT MAX(x.revision) FROM workflow_steps x
                  WHERE x.workflow_run_id=d.run_id AND x.step_key=d.depends_on_step_key)
            LEFT JOIN workflow_step_group_memberships m ON m.workflow_run_id=s.workflow_run_id AND m.step_id=s.id
            WHERE d.run_id=$run AND d.step_key=$item
            ORDER BY d.depends_on_step_key;
            """))
        {
            command.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(runId));
            command.Parameters.AddWithValue("$item", itemStepKey);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add(new BodyDependencyRow(reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetInt32(2),
                    reader.IsDBNull(3) ? null : (StepStatus)reader.GetInt32(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetInt32(6)));
            }
        }
        var actualDependencies = rows.Select(row => row.StepKey).ToHashSet(StringComparer.Ordinal);
        var hasBoundary = actualDependencies.Contains(boundaryStepKey);
        if (dependencies.Any(required => !actualDependencies.Contains(required)))
            throw new WorkflowStateException("A grouped fan-out body completion is missing an admitted static dependency.");
        var childRows = rows.Where(row => !dependencies.Contains(row.StepKey))
            .OrderBy(row => row.StepKey, StringComparer.Ordinal).ToArray();
        if (!hasBoundary || childRows.Length == 0)
            throw new WorkflowStateException("A grouped fan-out body completion has no valid selection dependency or body operation.");
        var childKeys = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>(childRows.Length);
        foreach (var row in childRows)
        {
            var isDescendant = row.OccurrenceId is not null && await IsOccurrenceDescendantAsync(connection, transaction,
                runId, row.OccurrenceId, itemOccurrenceId, cancellationToken).ConfigureAwait(false);
            if (row.StepId is null || row.Revision is null || row.Status != StepStatus.Completed ||
                row.InputHash is null || row.OccurrenceId is null || row.Role != (int)ActivityGroupMemberRole.Work ||
                !isDescendant)
                throw new WorkflowStateException("A grouped fan-out body operation is not complete in its admitted item scope.");
            childKeys.Add(row.StepKey);
            result.Add(SerializeBodyRevision(row.StepKey, row.StepId, row.Revision.Value,
                row.InputHash, row.OccurrenceId, row.Role.Value));
        }
        await ValidateBodyMembershipSetAsync(connection, transaction, runId, itemStepKey, itemOccurrenceId,
            childKeys, cancellationToken).ConfigureAwait(false);
        return result.AsReadOnly();
    }

    private static async ValueTask<bool> IsOccurrenceDescendantAsync(
        SqliteConnection connection, SqliteTransaction? transaction, Guid runId, string occurrenceId,
        string rootOccurrenceId, CancellationToken cancellationToken)
    {
        await using var command = SqliteStoreSupport.CreateCommand(connection, transaction, """
            WITH RECURSIVE chain(id,parent_id,coordinate,depth) AS (
                SELECT group_occurrence_id,parent_group_occurrence_id,execution_coordinate,0
                FROM workflow_group_occurrences WHERE workflow_run_id=$run AND group_occurrence_id=$occurrence
                UNION ALL
                SELECT p.group_occurrence_id,p.parent_group_occurrence_id,p.execution_coordinate,c.depth+1
                FROM workflow_group_occurrences p JOIN chain c
                  ON p.workflow_run_id=$run AND p.group_occurrence_id=c.parent_id
                WHERE c.parent_id IS NOT NULL AND c.id<>$root AND c.depth < 64
            )
            SELECT CASE WHEN EXISTS(SELECT 1 FROM chain WHERE id=$root)
                  AND NOT EXISTS(SELECT 1 FROM chain WHERE coordinate<>(SELECT execution_coordinate FROM workflow_group_occurrences
                      WHERE workflow_run_id=$run AND group_occurrence_id=$root)) THEN 1 ELSE 0 END;
            """);
        command.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(runId));
        command.Parameters.AddWithValue("$occurrence", occurrenceId);
        command.Parameters.AddWithValue("$root", rootOccurrenceId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) == 1;
    }

    private static async ValueTask ValidateBodyMembershipSetAsync(
        SqliteConnection connection, SqliteTransaction? transaction, Guid runId, string itemStepKey,
        string itemOccurrenceId, IReadOnlySet<string> childKeys, CancellationToken cancellationToken)
    {
        await using var command = SqliteStoreSupport.CreateCommand(connection, transaction, """
            WITH RECURSIVE descendants(id,coordinate) AS (
                SELECT group_occurrence_id,execution_coordinate FROM workflow_group_occurrences
                WHERE workflow_run_id=$run AND group_occurrence_id=$root
                UNION ALL
                SELECT child.group_occurrence_id,child.execution_coordinate
                FROM workflow_group_occurrences child JOIN descendants parent
                  ON child.workflow_run_id=$run AND child.parent_group_occurrence_id=parent.id
                  AND child.execution_coordinate=parent.coordinate
            )
            SELECT m.step_key,m.role
            FROM workflow_step_group_memberships m
            JOIN workflow_steps s ON s.workflow_run_id=m.workflow_run_id AND s.id=m.step_id
              AND s.step_key=m.step_key AND s.revision=m.step_revision
              AND s.revision=(SELECT MAX(x.revision) FROM workflow_steps x
                  WHERE x.workflow_run_id=s.workflow_run_id AND x.step_key=s.step_key)
            JOIN descendants d ON d.id=m.group_occurrence_id
            WHERE m.workflow_run_id=$run;
            """);
        command.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(runId));
        command.Parameters.AddWithValue("$root", itemOccurrenceId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var foundCompletion = false;
        var foundChildren = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var key = reader.GetString(0);
            var role = (ActivityGroupMemberRole)reader.GetInt32(1);
            if (key == itemStepKey)
            {
                if (role != ActivityGroupMemberRole.Control) throw new WorkflowStateException("The item completion membership is not Control.");
                foundCompletion = true;
                continue;
            }
            if (role != ActivityGroupMemberRole.Work || !childKeys.Contains(key))
                throw new WorkflowStateException("A fan-out body membership is not included in its item's completion dependencies.");
            foundChildren.Add(key);
        }
        if (!foundCompletion || !foundChildren.SetEquals(childKeys))
            throw new WorkflowStateException("The fan-out body completion does not cover every current item operation.");
    }

    private static string SerializeBodyRevision(string key, string stepId, int revision,
        string inputHash, string occurrenceId, int role) => JsonSerializer.Serialize(new
        {
            key,
            step = stepId,
            revision,
            inputHash,
            occurrence = occurrenceId,
            role = (int?)role
        });

    private static async ValueTask<ActivityGroupFanOutState> ReadStateAsync(
        SqliteConnection connection, SqliteTransaction? transaction, Guid runId, string expansionId,
        string fanOutKey, Guid boundaryStepId, int boundaryRevision, string planDigest, int itemCount,
        CancellationToken cancellationToken)
    {
        var complete = false;
        ClosureVector? current = null;
        try
        {
            current = await ReadCompletedItemsAsync(connection, transaction, runId, expansionId,
                itemCount, cancellationToken).ConfigureAwait(false);
        }
        catch (WorkflowStateException) { }
        if (current is not null)
        {
            var json = JsonSerializer.Serialize(current.Entries);
            var digest = Digest((current.IsBody ? BodyClosureDomain : ClosureDomain) + json);
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
            SELECT i.expansion_id,i.item_key,i.input_hash,i.group_occurrence_id,i.role,i.step_key,e.canonical_json
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
        var canonical = reader.GetString(6);
        return new ReservedItem(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.GetInt32(4), reader.GetString(5), ReadBodyContract(canonical), canonical);
    }

    private static async ValueTask<ReservedItem?> FindBodyItemAsync(
        SqliteConnection connection, SqliteTransaction transaction, Guid runId, string expansionId, string itemKey,
        CancellationToken cancellationToken)
    {
        await using var command = SqliteStoreSupport.CreateCommand(connection, transaction, """
            SELECT i.expansion_id,i.item_key,i.input_hash,i.group_occurrence_id,i.role,i.step_key,e.canonical_json
            FROM workflow_group_fanout_items i
            JOIN workflow_group_fanout_expansions e ON e.workflow_run_id=i.workflow_run_id AND e.expansion_id=i.expansion_id
            WHERE i.workflow_run_id=$run AND i.expansion_id=$expansion AND i.item_key=$item
              AND json_extract(e.canonical_json,'$.bodyContract') IS NOT NULL;
            """);
        command.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(runId));
        command.Parameters.AddWithValue("$expansion", expansionId);
        command.Parameters.AddWithValue("$item", itemKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        var canonical = reader.GetString(6);
        return new ReservedItem(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.GetInt32(4), reader.GetString(5), ReadBodyContract(canonical), canonical);
    }

    private static string? ReadBodyContract(string canonical)
    {
        using var json = JsonDocument.Parse(canonical);
        var root = json.RootElement;
        var version = root.GetProperty("version").GetInt32();
        if (version == 1) return null;
        if (version != 2 || !root.TryGetProperty("bodyContract", out var body) || body.ValueKind != JsonValueKind.String)
            throw new WorkflowStateException("A grouped fan-out plan has an unsupported body contract version.");
        return body.GetString();
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
        string OccurrenceId, int Role, string StepKey, string? BodyContract, string CanonicalJson);
    private sealed record BodyDependencyRow(string StepKey, string? StepId, int? Revision, StepStatus? Status,
        string? InputHash, string? OccurrenceId, int? Role);
    private sealed record CompletedItemRow(string ItemKey, string StepKey, string InputHash, string ItemOccurrenceId,
        int ItemRole, string StepId, int Revision, string MembershipOccurrenceId, int MembershipRole,
        string CurrentInputHash, string BoundaryStepKey);
    private sealed record ClosureVector(bool IsBody, IReadOnlyList<string> Entries);
}
