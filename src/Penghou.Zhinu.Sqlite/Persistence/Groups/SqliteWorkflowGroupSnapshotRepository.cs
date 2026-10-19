using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Penghou.Zhinu.Context;
using Penghou.Zhinu.Sqlite.Persistence;

namespace Penghou.Zhinu.Sqlite.Persistence.Groups;

/// <summary>Payload-free, bounded group graph reads from one SQLite snapshot.</summary>
internal sealed class SqliteWorkflowGroupSnapshotRepository(IZhinuSqliteDatabase factory)
{
    private const int FixedRowBytes = 160;
    private const string FanOutPlanDomain = "penghou.zhinu.activity-group-fanout-plan.v1\n";
    private const string FanOutBodyPlanDomain = "penghou.zhinu.activity-group-fanout-plan.v2\n";
    private const string FanOutClosureDomain = "penghou.zhinu.activity-group-fanout-closure.v1\n";
    private const string FanOutBodyClosureDomain = "penghou.zhinu.activity-group-fanout-closure.v2\n";

    public async ValueTask<WorkflowGroupQueryResult<WorkflowGroupSnapshotData>> ReadGroupSnapshotAsync(
        Guid workflowRunId, WorkflowGroupReadLimits limits, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(limits);
        limits.Validate();
        if (workflowRunId == Guid.Empty) throw new ArgumentException("Workflow ID must not be empty.", nameof(workflowRunId));

        await factory.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: true);
        try
        {
            if (!await RunExistsAsync(connection, transaction, workflowRunId, cancellationToken).ConfigureAwait(false))
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return new WorkflowGroupQueryResult<WorkflowGroupSnapshotData>(WorkflowGroupReadStatus.RunNotFound);
            }

            var rowCount = await CountRowsAsync(connection, transaction, workflowRunId, cancellationToken).ConfigureAwait(false);
            if (rowCount > limits.MaximumRows)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return LimitExceeded("The run exceeds the configured snapshot row limit.");
            }
            var estimatedBytes = await EstimateBytesAsync(connection, transaction, workflowRunId, cancellationToken)
                .ConfigureAwait(false);
            if (estimatedBytes > limits.MaximumBytes)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return LimitExceeded("The run exceeds the configured snapshot byte limit.");
            }
            var closureVectorRows = await CountClosureVectorRowsAsync(connection, transaction, workflowRunId, cancellationToken)
                .ConfigureAwait(false);
            var definitionRows = await CountDefinitionRowsAsync(connection, transaction, workflowRunId, cancellationToken)
                .ConfigureAwait(false);
            rowCount = checked(rowCount + closureVectorRows + definitionRows);
            estimatedBytes = checked(estimatedBytes + FixedRowBytes * (closureVectorRows + definitionRows));
            if (rowCount > limits.MaximumRows || estimatedBytes > limits.MaximumBytes)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return LimitExceeded("The run exceeds the configured snapshot row or byte limit.");
            }
            var referencedChildren = await ReadChildStartReferencesAsync(connection, transaction, workflowRunId,
                cancellationToken).ConfigureAwait(false);
            referencedChildren.UnionWith(await ReadWaitChildReferencesAsync(connection, transaction, workflowRunId,
                cancellationToken).ConfigureAwait(false));
            var referenceBudget = await EstimateExternalChildReferencesAsync(connection, transaction, workflowRunId,
                referencedChildren, cancellationToken).ConfigureAwait(false);
            rowCount += referenceBudget.Rows;
            estimatedBytes += referenceBudget.Bytes;
            if (rowCount > limits.MaximumRows || estimatedBytes > limits.MaximumBytes)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return LimitExceeded("The run's child references exceed the configured snapshot budget.");
            }
            var run = await ReadRunAsync(connection, transaction, workflowRunId, cancellationToken).ConfigureAwait(false)
                ?? throw new WorkflowStateException("The run disappeared inside a consistent read transaction.");

            var manifest = await ReadManifestAsync(connection, transaction, run.RunId, run.ManifestDigest,
                cancellationToken).ConfigureAwait(false);
            if (run.ManifestDigest is null && manifest is not null)
                throw new WorkflowStateException("An ungrouped run has a durable group manifest.");
            if (run.ManifestDigest is not null && manifest is null)
                throw new WorkflowStateException("The run's pinned group manifest is missing.");

            var occurrences = await ReadOccurrencesAsync(connection, transaction, run.RunId, manifest,
                cancellationToken).ConfigureAwait(false);
            var occurrenceIds = occurrences.Select(item => item.OccurrenceId).ToHashSet(StringComparer.Ordinal);
            var occurrenceById = occurrences.ToDictionary(item => item.OccurrenceId, StringComparer.Ordinal);
            await ValidateMembershipsAsync(connection, transaction, run.RunId, cancellationToken).ConfigureAwait(false);
            var steps = await ReadStepsAsync(connection, transaction, run.RunId, cancellationToken)
                .ConfigureAwait(false);
            foreach (var step in steps)
                if (step.GroupOccurrenceId is { } occurrenceId && !occurrenceIds.Contains(occurrenceId))
                    throw new WorkflowStateException("A step membership references a missing group occurrence.");
            var dependencies = await ReadDependenciesAsync(connection, transaction, run.RunId, cancellationToken)
                .ConfigureAwait(false);
            var waits = await ReadWaitsAsync(connection, transaction, run.RunId, cancellationToken)
                .ConfigureAwait(false);
            var approvals = await ReadApprovalsAsync(connection, transaction, run.RunId, cancellationToken)
                .ConfigureAwait(false);
            var expansions = await ReadExpansionsAsync(connection, transaction, run.RunId, manifest, occurrenceById, steps, dependencies,
                cancellationToken).ConfigureAwait(false);
            if (run.GroupFanOutUsed != (expansions.Count > 0))
                throw new WorkflowStateException("The run's grouped fan-out marker does not match its durable expansions.");
            var childRuns = await ReadChildRunsAsync(connection, transaction, run.RunId, referencedChildren,
                cancellationToken).ConfigureAwait(false);
            var sourceRun = run.SourceRunId is { } sourceId
                ? await ReadRunAsync(connection, transaction, sourceId, cancellationToken).ConfigureAwait(false)
                : null;
            var generation = await ReadGenerationAsync(connection, transaction, run.RunId, cancellationToken)
                .ConfigureAwait(false);
            var dispositions = generation is null
                ? Array.Empty<WorkflowGroupDispositionFact>()
                : await ReadDispositionsAsync(connection, transaction, generation.GenerationId, cancellationToken)
                    .ConfigureAwait(false);
            var operation = await ReadOperationAsync(connection, transaction, run.RunId, cancellationToken)
                .ConfigureAwait(false);
            var watermark = await ReadDurableWatermarkAsync(connection, transaction, run.RunId, cancellationToken)
                .ConfigureAwait(false);
            var createdSequence = await ReadCreatedSequenceAsync(connection, transaction, run.RunId, cancellationToken)
                .ConfigureAwait(false);

            IReadOnlyList<WorkflowGroupDefinitionFact> definitions = manifest is null
                ? Array.Empty<WorkflowGroupDefinitionFact>()
                : BuildDefinitions(manifest, occurrences, limits);
            var data = new WorkflowGroupSnapshotData
            {
                Run = run,
                ThroughDurableSequence = watermark,
                RunCreatedSequence = createdSequence,
                EstimatedBytes = estimatedBytes,
                Definitions = definitions,
                Occurrences = occurrences,
                Steps = BuildStepFacts(run.RunId, steps, waits),
                Dependencies = dependencies,
                Waits = waits,
                Approvals = approvals,
                Expansions = expansions,
                ChildRuns = childRuns,
                SourceRun = sourceRun,
                Generation = generation,
                Operation = operation,
                Dispositions = dispositions
            };
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return new WorkflowGroupQueryResult<WorkflowGroupSnapshotData>(WorkflowGroupReadStatus.Success, data);
        }
        catch (WorkflowStateException)
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            return new WorkflowGroupQueryResult<WorkflowGroupSnapshotData>(
                WorkflowGroupReadStatus.InconsistentData, Reason: "Durable group rows failed integrity validation.");
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or
            FormatException or OverflowException or KeyNotFoundException or WorkflowConfigurationException)
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            return new WorkflowGroupQueryResult<WorkflowGroupSnapshotData>(
                WorkflowGroupReadStatus.InconsistentData, Reason: "The durable group snapshot contains malformed data.");
        }
    }

    public async ValueTask<long?> GetGroupSnapshotRunStampAsync(Guid workflowRunId, CancellationToken cancellationToken)
    {
        if (workflowRunId == Guid.Empty) throw new ArgumentException("Workflow ID must not be empty.", nameof(workflowRunId));
        await factory.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: true);
        await using var command = SqliteStoreSupport.CreateCommand(connection, transaction, """
            SELECT (SELECT COUNT(*) FROM workflow_runs WHERE id=$run),
                   (SELECT MIN(sequence) FROM workflow_events WHERE workflow_run_id=$run AND event_type=$started);
            """);
        command.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(workflowRunId));
        command.Parameters.AddWithValue("$started", WorkflowEventTypes.WorkflowStarted);
        long? stamp;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || reader.GetInt64(0) == 0)
            {
                stamp = null;
            }
            else
            {
                stamp = reader.IsDBNull(1) ? 0L : reader.GetInt64(1);
            }
        }
        await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
        return stamp;
    }

    private static WorkflowGroupQueryResult<WorkflowGroupSnapshotData> LimitExceeded(string reason) =>
        new(WorkflowGroupReadStatus.LimitExceeded, Reason: reason);

    private static async ValueTask<bool> RunExistsAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid runId, CancellationToken cancellationToken)
    {
        await using var command = SqliteStoreSupport.CreateCommand(connection, transaction,
            "SELECT EXISTS(SELECT 1 FROM workflow_runs WHERE id=$run);");
        command.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(runId));
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture) != 0;
    }

    private static async ValueTask<WorkflowGroupRunFact?> ReadRunAsync(
        SqliteConnection connection, SqliteTransaction transaction, Guid runId, CancellationToken cancellationToken)
    {
        await using var command = SqliteStoreSupport.CreateCommand(connection, transaction, """
            SELECT id,workflow_name,workflow_version,status,parent_run_id,source_run_id,created_at,updated_at,
                   deadline,lease_generation,lease_expires_at,group_manifest_digest,group_fanout_used
            FROM workflow_runs WHERE id=$run;
            """);
        command.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(runId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        return new WorkflowGroupRunFact(
            Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2),
            ReadEnum<WorkflowStatus>(reader.GetInt32(3)), ReadGuid(reader, 4), ReadGuid(reader, 5),
            SqliteStoreSupport.ParseTimestamp(reader.GetString(6)), SqliteStoreSupport.ParseTimestamp(reader.GetString(7)),
            SqliteStoreSupport.ParseNullableTimestamp(reader, 8), reader.GetInt64(9),
            SqliteStoreSupport.ParseNullableTimestamp(reader, 10), SqliteStoreSupport.GetNullableString(reader, 11),
            reader.GetBoolean(12));
    }

    private static async ValueTask<long> CountRowsAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid runId, CancellationToken cancellationToken)
    {
        await using var command = SqliteStoreSupport.CreateCommand(connection, transaction, """
            SELECT 1
             + (SELECT COUNT(*) FROM workflow_group_manifests WHERE workflow_run_id=$run)
             + (SELECT COUNT(*) FROM workflow_group_occurrences WHERE workflow_run_id=$run)
             + (SELECT COUNT(*) FROM workflow_steps WHERE workflow_run_id=$run)
             + (SELECT COUNT(*) FROM workflow_step_dependencies WHERE run_id=$run)
             + (SELECT COUNT(*) FROM workflow_waits WHERE workflow_run_id=$run)
             + (SELECT COUNT(*) FROM workflow_authorization_checkpoints WHERE workflow_run_id=$run)
             + (SELECT COUNT(*) FROM workflow_group_fanout_expansions WHERE workflow_run_id=$run)
             + (SELECT COUNT(*) FROM workflow_group_fanout_items WHERE workflow_run_id=$run)
             + (SELECT COUNT(*) FROM workflow_group_fanout_closures WHERE workflow_run_id=$run)
             + (SELECT COUNT(*) FROM workflow_runs WHERE parent_run_id=$run)
             + (SELECT COUNT(*) FROM workflow_runs WHERE id=(SELECT source_run_id FROM workflow_runs WHERE id=$run))
             + (SELECT COUNT(*) FROM workflow_generations WHERE workflow_run_id=$run)
             + (SELECT COUNT(*) FROM workflow_generation_dispositions WHERE generation_id=(
                 SELECT generation_id FROM workflow_generations WHERE workflow_run_id=$run ORDER BY ordinal DESC LIMIT 1))
             + (SELECT COUNT(*) FROM workflow_run_operations WHERE workflow_run_id=$run AND status NOT IN ($completed,$failed));
            """);
        command.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(runId));
        command.Parameters.AddWithValue("$completed", (int)WorkflowOperationStatus.Completed);
        command.Parameters.AddWithValue("$failed", (int)WorkflowOperationStatus.Failed);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async ValueTask<long> CountClosureVectorRowsAsync(SqliteConnection connection,
        SqliteTransaction transaction, Guid runId, CancellationToken cancellationToken)
    {
        await using var command = SqliteStoreSupport.CreateCommand(connection, transaction, """
            SELECT COALESCE(SUM(CASE WHEN json_valid(current_steps_json)
                THEN json_array_length(current_steps_json) ELSE 1000001 END),0)
            FROM workflow_group_fanout_closures WHERE workflow_run_id=$run;
            """);
        command.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(runId));
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async ValueTask<long> CountDefinitionRowsAsync(SqliteConnection connection,
        SqliteTransaction transaction, Guid runId, CancellationToken cancellationToken)
    {
        await using var command = SqliteStoreSupport.CreateCommand(connection, transaction, """
            SELECT CASE WHEN json_valid(canonical_json)
                THEN COALESCE(json_array_length(canonical_json,'$.definitions'),0) ELSE 1000001 END
            FROM workflow_group_manifests WHERE workflow_run_id=$run;
            """);
        command.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(runId));
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null ? 0 : Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async ValueTask<long> EstimateBytesAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid runId, CancellationToken cancellationToken)
    {
        var childStartType = SerializationIdentity.TypeId(typeof(ChildRunCoordinator.ChildStartRequest));
        var guidType = SerializationIdentity.TypeId(typeof(Guid));
        await using var command = SqliteStoreSupport.CreateCommand(connection, transaction, """
            SELECT $fixed
             + COALESCE((SELECT SUM($fixed + length(CAST(canonical_json AS BLOB))) FROM workflow_group_manifests WHERE workflow_run_id=$run),0)
             + COALESCE((SELECT SUM($fixed + length(CAST(group_occurrence_id AS BLOB))+length(CAST(group_key AS BLOB))+
                   length(CAST(COALESCE(parent_group_occurrence_id,'') AS BLOB))+length(CAST(execution_coordinate AS BLOB))+
                   length(CAST(manifest_digest AS BLOB))) FROM workflow_group_occurrences WHERE workflow_run_id=$run),0)
             + COALESCE((SELECT SUM($fixed + length(CAST(s.step_key AS BLOB))+length(CAST(COALESCE(s.input_hash,'') AS BLOB))+
                   length(CAST(COALESCE(input_type,'') AS BLOB))+
                   length(CAST(COALESCE(s.output_type,'') AS BLOB))+length(CAST(COALESCE(m.group_occurrence_id,'') AS BLOB))+
                   CASE WHEN s.input_type=$childType AND s.output_type=$guidType
                        THEN length(CAST(COALESCE(s.output_json,'') AS BLOB)) ELSE 0 END)
                   FROM workflow_steps s LEFT JOIN workflow_step_group_memberships m
                     ON m.workflow_run_id=s.workflow_run_id AND m.step_id=s.id AND m.step_key=s.step_key AND m.step_revision=s.revision
                   WHERE s.workflow_run_id=$run),0)
             + COALESCE((SELECT SUM($fixed + length(CAST(step_key AS BLOB))+length(CAST(depends_on_step_key AS BLOB)))
                   FROM workflow_step_dependencies WHERE run_id=$run),0)
             + COALESCE((SELECT SUM($fixed + length(CAST(step_key AS BLOB))) FROM workflow_waits WHERE workflow_run_id=$run),0)
             + COALESCE((SELECT SUM($fixed + length(CAST(step_key AS BLOB))) FROM workflow_authorization_checkpoints WHERE workflow_run_id=$run),0)
             + COALESCE((SELECT SUM($fixed + length(CAST(canonical_json AS BLOB))) FROM workflow_group_fanout_expansions WHERE workflow_run_id=$run),0)
             + COALESCE((SELECT SUM($fixed + length(CAST(item_key AS BLOB))+length(CAST(step_key AS BLOB))+length(CAST(input_hash AS BLOB))+
                   length(CAST(input_hash AS BLOB))+
                   length(CAST(group_occurrence_id AS BLOB)))
                   FROM workflow_group_fanout_items WHERE workflow_run_id=$run),0)
             + COALESCE((SELECT SUM($fixed + length(CAST(current_steps_json AS BLOB))) FROM workflow_group_fanout_closures WHERE workflow_run_id=$run),0)
             + COALESCE((SELECT SUM($fixed + length(CAST(workflow_name AS BLOB))+length(CAST(workflow_version AS BLOB)))
                   FROM workflow_runs WHERE id=$run OR parent_run_id=$run OR id=(SELECT source_run_id FROM workflow_runs WHERE id=$run)),0)
             + COALESCE((SELECT SUM($fixed) FROM workflow_generations WHERE workflow_run_id=$run),0)
             + COALESCE((SELECT SUM($fixed) FROM workflow_generation_dispositions WHERE generation_id=(
                   SELECT generation_id FROM workflow_generations WHERE workflow_run_id=$run ORDER BY ordinal DESC LIMIT 1)),0)
             + COALESCE((SELECT SUM($fixed + length(CAST(operation_type AS BLOB))) FROM workflow_run_operations
                   WHERE workflow_run_id=$run AND status NOT IN ($completed,$failed)),0);
            """);
        command.Parameters.AddWithValue("$fixed", FixedRowBytes);
        command.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(runId));
        command.Parameters.AddWithValue("$childType", childStartType);
        command.Parameters.AddWithValue("$guidType", guidType);
        command.Parameters.AddWithValue("$completed", (int)WorkflowOperationStatus.Completed);
        command.Parameters.AddWithValue("$failed", (int)WorkflowOperationStatus.Failed);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async ValueTask<ActivityGroupManifest?> ReadManifestAsync(SqliteConnection connection,
        SqliteTransaction transaction, Guid runId, string? runPin, CancellationToken cancellationToken)
    {
        await using var command = SqliteStoreSupport.CreateCommand(connection, transaction, """
            SELECT contract_version,manifest_digest,length(CAST(canonical_json AS BLOB)),canonical_json
            FROM workflow_group_manifests WHERE workflow_run_id=$run;
            """);
        command.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(runId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        if (reader.GetInt32(2) > ActivityGroupManifestLimits.MaximumAllowedCanonicalBytes)
            throw new WorkflowStateException("The durable group manifest exceeds its size limit.");
        if (runPin is null || !string.Equals(runPin, reader.GetString(1), StringComparison.Ordinal))
            throw new WorkflowStateException("The durable group manifest does not match the run's immutable pin.");
        var manifest = RestoreManifest(reader.GetInt32(0), reader.GetString(1), reader.GetString(3));
        if (!string.Equals(manifest.ManifestDigest, runPin, StringComparison.Ordinal))
            throw new WorkflowStateException("The durable group manifest does not match the run's immutable pin.");
        return manifest;
    }

    private static ActivityGroupManifest RestoreManifest(int version, string digest, string canonical)
    {
        if (version != ActivityGroupManifest.CurrentContractVersion ||
            Encoding.UTF8.GetByteCount(canonical) > ActivityGroupManifestLimits.MaximumAllowedCanonicalBytes)
            throw new WorkflowStateException("The durable group manifest version or size is invalid.");
        using var document = JsonDocument.Parse(canonical, new JsonDocumentOptions { MaxDepth = 8 });
        var root = document.RootElement;
        if (root.GetProperty("contractVersion").GetInt32() != version) throw new WorkflowStateException("Group manifest version is inconsistent.");
        var definitions = new List<ActivityGroupDefinition>();
        foreach (var item in root.GetProperty("definitions").EnumerateArray())
        {
            ActivityGroupMacroProvenance? provenance = null;
            var p = item.GetProperty("provenance");
            if (p.ValueKind != JsonValueKind.Null)
                provenance = new ActivityGroupMacroProvenance(p.GetProperty("macroName").GetString()!,
                    p.GetProperty("macroDefinitionFingerprint").GetString()!, p.GetProperty("invocationKey").GetString()!,
                    Optional(p, "macroVersion"), Optional(p, "sourceReference"));
            definitions.Add(new ActivityGroupDefinition(item.GetProperty("groupKey").GetString()!,
                Enum.Parse<ActivityGroupKind>(item.GetProperty("kind").GetString()!, false),
                item.GetProperty("displayName").GetString()!, Optional(item, "parentGroupKey"),
                item.GetProperty("childOrdinal").GetInt32(), provenance));
        }
        var restored = ActivityGroupManifest.Create(definitions);
        if (restored.CanonicalJson != canonical || restored.ManifestDigest != digest)
            throw new WorkflowStateException("The durable group manifest canonical bytes or digest do not match.");
        return restored;
    }

    private static string? Optional(JsonElement element, string key) =>
        element.GetProperty(key).ValueKind == JsonValueKind.Null ? null : element.GetProperty(key).GetString();

    private static async ValueTask<IReadOnlyList<WorkflowGroupOccurrenceFact>> ReadOccurrencesAsync(
        SqliteConnection connection, SqliteTransaction transaction, Guid runId, ActivityGroupManifest? manifest,
        CancellationToken cancellationToken)
    {
        await using var command = SqliteStoreSupport.CreateCommand(connection, transaction, """
            SELECT group_occurrence_id,group_key,parent_group_occurrence_id,execution_coordinate,manifest_digest
            FROM workflow_group_occurrences WHERE workflow_run_id=$run ORDER BY group_occurrence_id;
            """);
        command.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(runId));
        var raw = new Dictionary<string, OccurrenceRow>(StringComparer.Ordinal);
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var id = reader.GetString(0);
                if (!raw.TryAdd(id, new OccurrenceRow(id, reader.GetString(1), ReadNullableString(reader, 2),
                        reader.GetString(3), reader.GetString(4))))
                    throw new WorkflowStateException("Duplicate group occurrence identity.");
            }
        }
        if (raw.Count > 0 && manifest is null) throw new WorkflowStateException("Ungrouped run has durable group occurrences.");
        if (raw.Count > ActivityGroupOccurrence.MaximumAllowedOccurrencesPerRun)
            throw new WorkflowStateException("The run exceeds its durable group occurrence limit.");
        var built = new Dictionary<string, ActivityGroupOccurrence>(StringComparer.Ordinal);
        var active = new HashSet<string>(StringComparer.Ordinal);
        ActivityGroupOccurrence Build(string id)
        {
            if (built.TryGetValue(id, out var cached)) return cached;
            if (!raw.TryGetValue(id, out var row)) throw new WorkflowStateException("A group occurrence has a missing parent.");
            if (!active.Add(id) || active.Count > ActivityGroupManifestLimits.MaximumAllowedDepth)
                throw new WorkflowStateException("The group occurrence hierarchy is cyclic or too deep.");
            if (row.ManifestDigest != manifest!.ManifestDigest) throw new WorkflowStateException("Occurrence manifest pin mismatch.");
            var parent = row.ParentId is null ? null : Build(row.ParentId);
            var occurrence = ActivityGroupOccurrence.Create(runId, manifest, row.GroupKey, parent, row.Coordinate);
            active.Remove(id);
            if (occurrence.GroupOccurrenceId != row.Id) throw new WorkflowStateException("Occurrence ID does not match its canonical coordinate.");
            return built[id] = occurrence;
        }
        var facts = new List<WorkflowGroupOccurrenceFact>(raw.Count);
        foreach (var row in raw.Values)
        {
            _ = Build(row.Id);
            facts.Add(new WorkflowGroupOccurrenceFact(runId, row.Id, row.GroupKey, row.ParentId, row.Coordinate));
        }
        return facts.AsReadOnly();
    }

    private static IReadOnlyList<WorkflowGroupDefinitionFact> BuildDefinitions(
        ActivityGroupManifest manifest, IReadOnlyList<WorkflowGroupOccurrenceFact> occurrences, WorkflowGroupReadLimits limits)
    {
        var counts = occurrences.GroupBy(item => item.GroupKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        return Array.AsReadOnly(manifest.Definitions.Select(definition => new WorkflowGroupDefinitionFact(
            definition.GroupKey, definition.ParentGroupKey, definition.Kind, definition.DisplayName, definition.ChildOrdinal,
            limits.IncludeProvenance && definition.Provenance is { } provenance
                ? new WorkflowGroupProvenance(provenance.MacroName, provenance.MacroDefinitionFingerprint,
                    provenance.InvocationKey, provenance.MacroVersion,
                    limits.IncludeSourceReferences ? provenance.SourceReference : null)
                : null,
            counts.GetValueOrDefault(definition.GroupKey))).ToArray());
    }

    private static async ValueTask ValidateMembershipsAsync(SqliteConnection connection,
        SqliteTransaction transaction, Guid runId, CancellationToken cancellationToken)
    {
        // An exact LEFT JOIN alone would turn damaged membership into ungrouped work.
        // Check both directions: orphan rows owned by this run and foreign rows on its steps.
        await using var command = SqliteStoreSupport.CreateCommand(connection, transaction, """
            SELECT EXISTS(SELECT 1 FROM workflow_step_group_memberships m
                LEFT JOIN workflow_steps s ON s.id=m.step_id
                WHERE m.workflow_run_id=$run AND (s.id IS NULL OR s.workflow_run_id<>m.workflow_run_id
                    OR s.step_key<>m.step_key OR s.revision<>m.step_revision))
             OR EXISTS(SELECT 1 FROM workflow_steps s JOIN workflow_step_group_memberships m ON m.step_id=s.id
                WHERE s.workflow_run_id=$run AND m.workflow_run_id<>s.workflow_run_id);
            """);
        command.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(runId));
        if (Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture) != 0)
            throw new WorkflowStateException("A group membership does not match its owning step revision.");
    }

    private static async ValueTask<IReadOnlyList<StepFactRow>> ReadStepsAsync(SqliteConnection connection,
        SqliteTransaction transaction, Guid runId, CancellationToken cancellationToken)
    {
        var childStartType = SerializationIdentity.TypeId(typeof(ChildRunCoordinator.ChildStartRequest));
        var guidType = SerializationIdentity.TypeId(typeof(Guid));
        await using var command = SqliteStoreSupport.CreateCommand(connection, transaction, """
            SELECT s.id,s.step_key,s.revision,s.attempt,s.status,s.lease_generation,s.lease_expires_at,
                   s.available_at,s.started_at,s.completed_at,s.input_hash,s.input_type,s.output_type,
                   CASE WHEN s.input_type=$childType AND s.output_type=$guidType THEN s.output_json ELSE NULL END,
                   m.group_occurrence_id,m.role
            FROM workflow_steps s
            LEFT JOIN workflow_step_group_memberships m ON m.workflow_run_id=s.workflow_run_id AND m.step_id=s.id
                AND m.step_key=s.step_key AND m.step_revision=s.revision
            WHERE s.workflow_run_id=$run ORDER BY s.created_at,s.step_key,s.revision;
            """);
        command.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(runId));
        command.Parameters.AddWithValue("$childType", childStartType);
        command.Parameters.AddWithValue("$guidType", guidType);
        var rows = new List<StepFactRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            Guid? childId = null;
            if (!reader.IsDBNull(13))
            {
                try { childId = JsonSerializer.Deserialize<Guid>(reader.GetString(13)); }
                catch (JsonException) { throw new WorkflowStateException("A child-start step has a malformed durable child reference."); }
            }
            rows.Add(new StepFactRow(
                Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetInt32(2), reader.GetInt32(3),
                ReadEnum<StepStatus>(reader.GetInt32(4)), reader.GetInt64(5), SqliteStoreSupport.ParseNullableTimestamp(reader, 6),
                SqliteStoreSupport.ParseNullableTimestamp(reader, 7), SqliteStoreSupport.ParseNullableTimestamp(reader, 8),
                SqliteStoreSupport.ParseNullableTimestamp(reader, 9), ReadNullableString(reader, 10), childId,
                ReadNullableString(reader, 14), reader.IsDBNull(15) ? null : ReadEnum<ActivityGroupMemberRole>(reader.GetInt32(15))));
        }
        var current = rows.GroupBy(row => row.StepKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Max(row => row.Revision), StringComparer.Ordinal);
        return rows.Select(row => row with { IsCurrent = row.Revision == current[row.StepKey] }).ToArray();
    }

    private static async ValueTask<IReadOnlyList<StepDependency>> ReadDependenciesAsync(SqliteConnection connection,
        SqliteTransaction transaction, Guid runId, CancellationToken cancellationToken)
    {
        await using var command = SqliteStoreSupport.CreateCommand(connection, transaction, """
            SELECT step_key,depends_on_step_key FROM workflow_step_dependencies WHERE run_id=$run
            ORDER BY step_key,depends_on_step_key;
            """);
        command.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(runId));
        var result = new List<StepDependency>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(new StepDependency(reader.GetString(0), reader.GetString(1)));
        return result.AsReadOnly();
    }

    private static async ValueTask<IReadOnlyList<WorkflowGroupWaitFact>> ReadWaitsAsync(SqliteConnection connection,
        SqliteTransaction transaction, Guid runId, CancellationToken cancellationToken)
    {
        await using var command = SqliteStoreSupport.CreateCommand(connection, transaction, """
            SELECT wait_id,step_id,step_key,step_revision,kind,status,child_run_id,deadline_at,available_at
            FROM workflow_waits WHERE workflow_run_id=$run ORDER BY created_at,step_key;
            """);
        command.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(runId));
        var result = new List<WorkflowGroupWaitFact>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(new WorkflowGroupWaitFact(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)),
                reader.GetString(2), reader.GetInt32(3), ReadEnum<WaitKind>(reader.GetInt32(4)),
                ReadEnum<WaitStatus>(reader.GetInt32(5)), ReadGuid(reader, 6),
                SqliteStoreSupport.ParseNullableTimestamp(reader, 7), SqliteStoreSupport.ParseNullableTimestamp(reader, 8)));
        return result.AsReadOnly();
    }

    private static async ValueTask<IReadOnlyList<WorkflowGroupApprovalFact>> ReadApprovalsAsync(SqliteConnection connection,
        SqliteTransaction transaction, Guid runId, CancellationToken cancellationToken)
    {
        await using var command = SqliteStoreSupport.CreateCommand(connection, transaction, """
            SELECT DISTINCT p.step_key,p.revision,p.is_compensation,p.decision,p.ready
            FROM workflow_authorization_checkpoints p
            JOIN workflow_runs r ON r.id=p.workflow_run_id
            WHERE p.workflow_run_id=$run AND p.decision=$approval
              AND ((p.is_compensation=0 AND EXISTS (SELECT 1 FROM workflow_steps s WHERE s.id=p.claim_id
                    AND s.revision=p.revision AND s.status IN ($running,$waiting) AND (p.ready=1 OR
                        (s.lease_generation=p.lease_generation AND r.lease_generation=p.lease_generation))))
                OR (p.is_compensation=1 AND EXISTS (SELECT 1 FROM workflow_step_compensations c WHERE c.id=p.claim_id
                    AND c.revision=p.revision AND c.status IN ($compRunning,$compPending) AND (p.ready=1 OR
                        (c.lease_generation=p.lease_generation AND r.lease_generation=p.lease_generation)))))
            ORDER BY p.step_key,p.revision,p.is_compensation;
            """);
        command.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(runId));
        // Match HasPendingAuthorizationsAsync: stale unready checkpoints do not park
        // current work, while durable ready wakeups survive lease rollover.
        command.Parameters.AddWithValue("$approval", (int)Penghou.Workflow.Abstractions.ExecutionAuthorizationDecision.ApprovalRequired);
        command.Parameters.AddWithValue("$running", (int)StepStatus.Running);
        command.Parameters.AddWithValue("$waiting", (int)StepStatus.Waiting);
        command.Parameters.AddWithValue("$compRunning", (int)CompensationStatus.Running);
        command.Parameters.AddWithValue("$compPending", (int)CompensationStatus.Pending);
        var result = new List<WorkflowGroupApprovalFact>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(new WorkflowGroupApprovalFact(reader.GetString(0), reader.GetInt32(1), reader.GetBoolean(2),
                ReadEnum<Penghou.Workflow.Abstractions.ExecutionAuthorizationDecision>(reader.GetInt32(3)), reader.GetBoolean(4)));
        return result.AsReadOnly();
    }

    private static async ValueTask<IReadOnlyList<WorkflowGroupExpansionFact>> ReadExpansionsAsync(
        SqliteConnection connection, SqliteTransaction transaction, Guid runId, ActivityGroupManifest? manifest,
        IReadOnlyDictionary<string, WorkflowGroupOccurrenceFact> occurrenceById, IReadOnlyList<StepFactRow> steps,
        IReadOnlyList<StepDependency> dependencies,
        CancellationToken cancellationToken)
    {
        var currentSteps = steps.Where(row => row.IsCurrent).ToDictionary(row => row.StepKey, StringComparer.Ordinal);
        await using var command = SqliteStoreSupport.CreateCommand(connection, transaction, """
            SELECT expansion_id,fan_out_key,boundary_step_id,boundary_step_key,boundary_revision,plan_digest,item_count,canonical_json
            FROM workflow_group_fanout_expansions WHERE workflow_run_id=$run ORDER BY fan_out_key,boundary_revision,expansion_id;
            """);
        command.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(runId));
        var headers = new List<ExpansionHeader>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                headers.Add(new ExpansionHeader(reader.GetString(0), reader.GetString(1), Guid.Parse(reader.GetString(2)),
                    reader.GetString(3), reader.GetInt32(4), reader.GetString(5), reader.GetInt32(6), reader.GetString(7)));
        }
        var result = new List<WorkflowGroupExpansionFact>(headers.Count);
        foreach (var header in headers)
        {
            var expansion = header.ExpansionId;
            var fanOutKey = header.FanOutKey;
            var boundaryId = header.BoundaryStepId;
            var boundaryKey = header.BoundaryStepKey;
            var boundaryRevision = header.BoundaryRevision;
            var digest = header.PlanDigest;
            var itemCount = header.ItemCount;
            var canonical = header.CanonicalJson;
            JsonElement[] planItems;
            ActivityGroupMemberRole planRole;
            string planGroup;
            ActivityGroupFanOutIdentityKind planKind;
            string? planParent;
            string? bodyContract;
            string[] planDependencies;
            using (var plan = JsonDocument.Parse(canonical, new JsonDocumentOptions { MaxDepth = 16 }))
            {
                var root = plan.RootElement;
                var planVersion = root.GetProperty("version").GetInt32();
                var planDomain = planVersion switch
                {
                    1 => FanOutPlanDomain,
                    2 => FanOutBodyPlanDomain,
                    _ => throw new WorkflowStateException("A fan-out plan version is unsupported.")
                };
                if (Digest(planDomain + canonical) != digest)
                    throw new WorkflowStateException("A fan-out plan digest does not match its canonical bytes.");
                bodyContract = planVersion == 2 && root.TryGetProperty("bodyContract", out var body)
                    ? body.GetString()
                    : null;
                if (planVersion == 2 && string.IsNullOrWhiteSpace(bodyContract))
                    throw new WorkflowStateException("A fan-out body plan omits its stable body contract.");
                if (root.GetProperty("run").GetString() != SqliteStoreSupport.Format(runId) ||
                    root.GetProperty("fanOut").GetString() != fanOutKey ||
                    root.GetProperty("selection").GetString() != boundaryKey ||
                    root.GetProperty("items").GetArrayLength() != itemCount)
                    throw new WorkflowStateException("A fan-out plan header is inconsistent with its durable row.");
                if (manifest is null || root.GetProperty("manifest").GetString() != manifest.ManifestDigest)
                    throw new WorkflowStateException("A fan-out plan does not match the run's pinned manifest.");
                planRole = ReadEnum<ActivityGroupMemberRole>(root.GetProperty("role").GetInt32());
                if (bodyContract is not null && planRole != ActivityGroupMemberRole.Control)
                    throw new WorkflowStateException("A grouped fan-out body plan requires Control membership for item completion.");
                planGroup = root.GetProperty("group").GetString()!;
                planKind = ReadEnum<ActivityGroupFanOutIdentityKind>(root.GetProperty("kind").GetInt32());
                planParent = root.GetProperty("parent").ValueKind == JsonValueKind.Null
                    ? null
                    : root.GetProperty("parent").GetString();
                planDependencies = root.GetProperty("dependencies").EnumerateArray()
                    .Select(item => item.GetString()!).ToArray();
                planItems = root.GetProperty("items").EnumerateArray().Select(element => element.Clone()).ToArray();
            }
            if (manifest is null)
                throw new WorkflowStateException("A fan-out plan has no pinned group manifest.");
            var groupDefinition = manifest.FindDefinition(planGroup)
                ?? throw new WorkflowStateException("A fan-out plan references a missing group definition.");
            if (planParent is null)
            {
                if (groupDefinition.ParentGroupKey is not null)
                    throw new WorkflowStateException("A fan-out plan omits its required parent occurrence.");
            }
            else if (groupDefinition.ParentGroupKey is null ||
                     !occurrenceById.TryGetValue(planParent, out var parentOccurrence) ||
                     parentOccurrence.GroupKey != groupDefinition.ParentGroupKey ||
                     parentOccurrence.ExecutionCoordinate != "[]")
            {
                throw new WorkflowStateException("A fan-out plan's parent occurrence is inconsistent with its group definition.");
            }
            var expectedExpansionId = Digest("penghou.zhinu.activity-group-fanout-expansion.v1\n" +
                runId.ToString("D") + "\n" + boundaryId.ToString("D") + "\n" + fanOutKey);
            if (expectedExpansionId != expansion) throw new WorkflowStateException("A fan-out expansion ID is not canonical.");
            var isCurrent = currentSteps.TryGetValue(boundaryKey, out var boundary) &&
                boundary.Id == boundaryId && boundary.Revision == boundaryRevision;
            var items = await ReadFanOutItemsAsync(connection, transaction, runId, expansion, itemCount, cancellationToken)
                .ConfigureAwait(false);
            if (planItems.Length != items.Count) throw new WorkflowStateException("Fan-out canonical items are incomplete.");
            // Canonical plans use UTF-16 ordinal ordering; SQLite BINARY uses UTF-8.
            // Compare keyed content without changing the durable closure vector order.
            var planItemsByKey = planItems.ToDictionary(item => item.GetProperty("key").GetString()!, StringComparer.Ordinal);
            for (var index = 0; index < items.Count; index++)
            {
                var stored = items[index];
                if (!planItemsByKey.TryGetValue(stored.Fact.ItemKey, out var planItem) ||
                    planItem.GetProperty("step").GetString() != stored.Fact.StepKey ||
                    planItem.GetProperty("input").GetString() != stored.InputHash ||
                    planItem.GetProperty("occurrence").GetString() != stored.Fact.OccurrenceId ||
                    stored.Fact.Role != planRole || !occurrenceById.TryGetValue(stored.Fact.OccurrenceId, out var occurrence) ||
                    occurrence.GroupKey != planGroup || occurrence.ParentOccurrenceId != planParent ||
                    !string.Equals(occurrence.ExecutionCoordinate,
                        ActivityGroupOccurrence.FanOutCoordinate(fanOutKey, planKind, stored.Fact.ItemKey),
                        StringComparison.Ordinal))
                    throw new WorkflowStateException("Fan-out item rows do not match the immutable canonical plan.");
            }
            var closures = await ReadClosuresAsync(connection, transaction, runId, expansion, bodyContract is not null, cancellationToken)
                .ConfigureAwait(false);
            var isComplete = isCurrent && IsExpansionClosed(items, closures, currentSteps, dependencies,
                occurrenceById, planDependencies, bodyContract);
            result.Add(new WorkflowGroupExpansionFact(expansion, fanOutKey, boundaryId, boundaryRevision,
                digest, itemCount, isCurrent, isComplete, Array.AsReadOnly(items.Select(item => item.Fact).ToArray()), closures)
            { BodyContract = bodyContract, RequiredDependencies = Array.AsReadOnly(planDependencies) });
        }
        return result.AsReadOnly();
    }

    private static async ValueTask<IReadOnlyList<FanOutItemRow>> ReadFanOutItemsAsync(
        SqliteConnection connection, SqliteTransaction transaction, Guid runId, string expansionId, int expectedCount,
        CancellationToken cancellationToken)
    {
        await using var command = SqliteStoreSupport.CreateCommand(connection, transaction, """
            SELECT item_key,step_key,input_hash,group_occurrence_id,role FROM workflow_group_fanout_items
            WHERE workflow_run_id=$run AND expansion_id=$expansion ORDER BY item_key;
            """);
        command.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(runId));
        command.Parameters.AddWithValue("$expansion", expansionId);
        var result = new List<FanOutItemRow>(expectedCount);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var fact = new WorkflowGroupFanOutItemFact(reader.GetString(0), reader.GetString(1), reader.GetString(3),
                ReadEnum<ActivityGroupMemberRole>(reader.GetInt32(4)))
            { InputHash = reader.GetString(2) };
            result.Add(new FanOutItemRow(fact, reader.GetString(2)));
        }
        if (result.Count != expectedCount) throw new WorkflowStateException("A fan-out item count is inconsistent.");
        return result.AsReadOnly();
    }

    private static async ValueTask<IReadOnlyList<WorkflowGroupClosureFact>> ReadClosuresAsync(
        SqliteConnection connection, SqliteTransaction transaction, Guid runId, string expansionId, bool bodyMode,
        CancellationToken cancellationToken)
    {
        await using var command = SqliteStoreSupport.CreateCommand(connection, transaction, """
            SELECT current_steps_json,closure_digest,closure_generation,completed_at FROM workflow_group_fanout_closures
            WHERE workflow_run_id=$run AND expansion_id=$expansion ORDER BY completed_at,closure_digest;
            """);
        command.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(runId));
        command.Parameters.AddWithValue("$expansion", expansionId);
        var result = new List<WorkflowGroupClosureFact>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var json = reader.GetString(0);
            var digest = reader.GetString(1);
            if (Digest((bodyMode ? FanOutBodyClosureDomain : FanOutClosureDomain) + json) != digest)
                throw new WorkflowStateException("A fan-out closure digest does not match its current-step vector.");
            result.Add(new WorkflowGroupClosureFact(digest, reader.GetInt64(2),
                SqliteStoreSupport.ParseTimestamp(reader.GetString(3)), ParseClosureVector(json, bodyMode)));
        }
        return result.AsReadOnly();
    }

    private static IReadOnlyList<WorkflowGroupRevisionFact> ParseClosureVector(string json, bool bodyMode)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 4 });
        if (document.RootElement.ValueKind != JsonValueKind.Array) throw new WorkflowStateException("Fan-out closure vector is not an array.");
        var result = new List<WorkflowGroupRevisionFact>();
        foreach (var entry in document.RootElement.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.String) throw new WorkflowStateException("Fan-out closure vector entry is malformed.");
            using var item = JsonDocument.Parse(entry.GetString()!, new JsonDocumentOptions { MaxDepth = 2 });
            var root = item.RootElement;
            var revision = new WorkflowGroupRevisionFact(Guid.Parse(root.GetProperty("step").GetString()!),
                root.GetProperty("revision").GetInt32());
            if (bodyMode)
                revision = revision with
                {
                    StepKey = root.GetProperty("key").GetString(),
                    InputHash = root.GetProperty("inputHash").GetString(),
                    OccurrenceId = root.GetProperty("occurrence").GetString(),
                    Role = root.GetProperty("role").ValueKind == JsonValueKind.Null
                        ? null
                        : ReadEnum<ActivityGroupMemberRole>(root.GetProperty("role").GetInt32())
                };
            result.Add(revision);
        }
        return result.AsReadOnly();
    }

    private static bool IsExpansionClosed(
        IReadOnlyList<FanOutItemRow> items, IReadOnlyList<WorkflowGroupClosureFact> closures,
        IReadOnlyDictionary<string, StepFactRow> currentSteps, IReadOnlyList<StepDependency> dependencies,
        IReadOnlyDictionary<string, WorkflowGroupOccurrenceFact> occurrences, IReadOnlyCollection<string> planDependencies,
        string? bodyContract)
    {
        if (bodyContract is not null)
        {
            var vector = BuildBodyClosureVector(items, currentSteps, dependencies, occurrences, planDependencies);
            if (vector is null) return false;
            var json = JsonSerializer.Serialize(vector);
            var digest = Digest(FanOutBodyClosureDomain + json);
            return closures.Any(closure => closure.Digest == digest &&
                closure.Steps.Count == vector.Count && VectorMatches(closure.Steps, vector));
        }
        var selected = new List<string>(items.Count);
        foreach (var storedItem in items)
        {
            var item = storedItem.Fact;
            if (!currentSteps.TryGetValue(item.StepKey, out var step) || step.Status != StepStatus.Completed ||
                step.GroupOccurrenceId != item.OccurrenceId || step.Role != item.Role || step.InputHash != storedItem.InputHash)
                return false;
            selected.Add(JsonSerializer.Serialize(new { key = item.ItemKey, step = step.Id.ToString("D"), revision = step.Revision }));
        }
        var currentJson = JsonSerializer.Serialize(selected);
        var closureDigest = Digest(FanOutClosureDomain + currentJson);
        return closures.Any(closure => closure.Digest == closureDigest &&
            closure.Steps.Count == items.Count && VectorMatches(closure.Steps, items, currentSteps));
    }

    private static IReadOnlyList<string>? BuildBodyClosureVector(
        IReadOnlyList<FanOutItemRow> items, IReadOnlyDictionary<string, StepFactRow> currentSteps,
        IReadOnlyList<StepDependency> dependencies, IReadOnlyDictionary<string, WorkflowGroupOccurrenceFact> occurrences,
        IReadOnlyCollection<string> planDependencies)
    {
        var vector = new List<string>();
        foreach (var stored in items)
        {
            var item = stored.Fact;
            if (!currentSteps.TryGetValue(item.StepKey, out var completion) || completion.Status != StepStatus.Completed ||
                completion.GroupOccurrenceId != item.OccurrenceId || completion.Role != ActivityGroupMemberRole.Control ||
                completion.InputHash != stored.InputHash || !occurrences.TryGetValue(item.OccurrenceId, out var rootOccurrence))
                return null;
            var allDependencyKeys = dependencies.Where(edge => edge.StepKey == item.StepKey)
                .Select(edge => edge.DependsOnStepKey).ToHashSet(StringComparer.Ordinal);
            if (planDependencies.Any(key => !allDependencyKeys.Contains(key))) return null;
            var childKeys = allDependencyKeys.Where(key => !planDependencies.Contains(key, StringComparer.Ordinal))
                .Where(key => key != item.StepKey)
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            if (childKeys.Length == 0)
                return null;
            vector.Add(SerializeBodyRevision(item.StepKey, completion));
            foreach (var childKey in childKeys)
            {
                if (!currentSteps.TryGetValue(childKey, out var child) || child.Status != StepStatus.Completed ||
                    child.Role != ActivityGroupMemberRole.Work || child.GroupOccurrenceId is null ||
                    !IsOccurrenceUnder(occurrences, child.GroupOccurrenceId, item.OccurrenceId, rootOccurrence.ExecutionCoordinate))
                    return null;
                vector.Add(SerializeBodyRevision(childKey, child));
            }
            var dependencySet = childKeys.ToHashSet(StringComparer.Ordinal);
            foreach (var member in currentSteps.Values.Where(step => step.GroupOccurrenceId is not null &&
                         IsOccurrenceUnder(occurrences, step.GroupOccurrenceId, item.OccurrenceId, rootOccurrence.ExecutionCoordinate)))
                if (member.StepKey != item.StepKey && !dependencySet.Contains(member.StepKey))
                    return null;
        }
        return vector.AsReadOnly();
    }

    private static bool IsOccurrenceUnder(IReadOnlyDictionary<string, WorkflowGroupOccurrenceFact> occurrences,
        string occurrenceId, string rootOccurrenceId, string coordinate)
    {
        for (var currentId = occurrenceId; occurrences.TryGetValue(currentId, out var current); currentId = current.ParentOccurrenceId!)
        {
            if (current.ExecutionCoordinate != coordinate) return false;
            if (currentId == rootOccurrenceId) return true;
            if (current.ParentOccurrenceId is null) return false;
        }
        return false;
    }

    private static string SerializeBodyRevision(string key, StepFactRow step) => JsonSerializer.Serialize(new
    {
        key,
        step = step.Id.ToString("D"),
        revision = step.Revision,
        inputHash = step.InputHash,
        occurrence = step.GroupOccurrenceId,
        role = (int?)step.Role
    });

    private static bool VectorMatches(IReadOnlyList<WorkflowGroupRevisionFact> vector,
        IReadOnlyList<string> expected)
    {
        if (vector.Count != expected.Count) return false;
        for (var index = 0; index < expected.Count; index++)
        {
            using var json = JsonDocument.Parse(expected[index], new JsonDocumentOptions { MaxDepth = 2 });
            if (vector[index].StepId != Guid.Parse(json.RootElement.GetProperty("step").GetString()!) ||
                vector[index].Revision != json.RootElement.GetProperty("revision").GetInt32()) return false;
        }
        return true;
    }

    private static bool VectorMatches(IReadOnlyList<WorkflowGroupRevisionFact> vector,
        IReadOnlyList<FanOutItemRow> items, IReadOnlyDictionary<string, StepFactRow> currentSteps)
    {
        if (vector.Count != items.Count) return false;
        for (var index = 0; index < items.Count; index++)
        {
            if (!currentSteps.TryGetValue(items[index].Fact.StepKey, out var step) ||
                vector[index].StepId != step.Id || vector[index].Revision != step.Revision) return false;
        }
        return true;
    }

    private static async ValueTask<HashSet<Guid>> ReadChildStartReferencesAsync(SqliteConnection connection,
        SqliteTransaction transaction, Guid runId, CancellationToken cancellationToken)
    {
        var childStartType = SerializationIdentity.TypeId(typeof(ChildRunCoordinator.ChildStartRequest));
        var guidType = SerializationIdentity.TypeId(typeof(Guid));
        await using var command = SqliteStoreSupport.CreateCommand(connection, transaction, """
            SELECT output_json FROM workflow_steps WHERE workflow_run_id=$run AND input_type=$childType AND output_type=$guidType
                AND output_json IS NOT NULL;
            """);
        command.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(runId));
        command.Parameters.AddWithValue("$childType", childStartType);
        command.Parameters.AddWithValue("$guidType", guidType);
        var result = new HashSet<Guid>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                var childId = JsonSerializer.Deserialize<Guid>(reader.GetString(0));
                if (childId == Guid.Empty) throw new WorkflowStateException("A child-start step has an empty durable child reference.");
                result.Add(childId);
            }
            catch (JsonException) { throw new WorkflowStateException("A child-start step has a malformed durable child reference."); }
        }
        return result;
    }

    private static async ValueTask<HashSet<Guid>> ReadWaitChildReferencesAsync(SqliteConnection connection,
        SqliteTransaction transaction, Guid runId, CancellationToken cancellationToken)
    {
        await using var command = SqliteStoreSupport.CreateCommand(connection, transaction,
            "SELECT DISTINCT child_run_id FROM workflow_waits WHERE workflow_run_id=$run AND child_run_id IS NOT NULL;");
        command.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(runId));
        var result = new HashSet<Guid>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) result.Add(Guid.Parse(reader.GetString(0)));
        return result;
    }

    private static async ValueTask<(long Rows, long Bytes)> EstimateExternalChildReferencesAsync(
        SqliteConnection connection, SqliteTransaction transaction, Guid runId, IReadOnlySet<Guid> childIds,
        CancellationToken cancellationToken)
    {
        long rows = 0, bytes = 0;
        foreach (var chunk in childIds.Chunk(500))
        {
            var parameters = chunk.Select((_, index) => "$id" + index).ToArray();
            var sql = $"SELECT id,parent_run_id,length(CAST(workflow_name AS BLOB)),length(CAST(workflow_version AS BLOB)) " +
                $"FROM workflow_runs WHERE id IN ({string.Join(',', parameters)});";
            await using var command = SqliteStoreSupport.CreateCommand(connection, transaction, sql);
            var index = 0;
            foreach (var id in chunk) command.Parameters.AddWithValue(parameters[index++], SqliteStoreSupport.Format(id));
            var directIds = new HashSet<Guid>();
            await using (var direct = SqliteStoreSupport.CreateCommand(connection, transaction,
                $"SELECT id FROM workflow_runs WHERE parent_run_id=$run AND id IN ({string.Join(',', parameters)});"))
            {
                direct.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(runId));
                index = 0;
                foreach (var id in chunk) direct.Parameters.AddWithValue(parameters[index++], SqliteStoreSupport.Format(id));
                await using var directReader = await direct.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await directReader.ReadAsync(cancellationToken).ConfigureAwait(false)) directIds.Add(Guid.Parse(directReader.GetString(0)));
            }
            var found = new Dictionary<Guid, (long Name, long Version)>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                found[Guid.Parse(reader.GetString(0))] = (reader.GetInt64(2), reader.GetInt64(3));
            }
            foreach (var id in chunk)
            {
                if (directIds.Contains(id)) continue;
                rows++;
                bytes += FixedRowBytes;
                if (found.TryGetValue(id, out var size)) bytes += size.Name + size.Version;
            }
        }
        return (rows, bytes);
    }

    private static async ValueTask<IReadOnlyList<WorkflowGroupChildRunFact>> ReadChildRunsAsync(
        SqliteConnection connection, SqliteTransaction transaction, Guid runId, IReadOnlySet<Guid> childReferences,
        CancellationToken cancellationToken)
    {
        var references = childReferences;
        var rows = new Dictionary<Guid, WorkflowGroupChildRunFact>();
        await using (var direct = SqliteStoreSupport.CreateCommand(connection, transaction, """
            SELECT id,parent_run_id,status FROM workflow_runs WHERE parent_run_id=$run ORDER BY created_at,id;
            """))
        {
            direct.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(runId));
            await using var reader = await direct.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var id = Guid.Parse(reader.GetString(0));
                rows[id] = new WorkflowGroupChildRunFact(id, ReadGuid(reader, 1), ReadEnum<WorkflowStatus>(reader.GetInt32(2)), true);
            }
        }
        const int chunkSize = 500;
        foreach (var chunk in references.Except(rows.Keys).Chunk(chunkSize))
        {
            var parameters = chunk.Select((_, index) => "$id" + index).ToArray();
            var sql = $"SELECT id,parent_run_id,status FROM workflow_runs WHERE id IN ({string.Join(',', parameters)});";
            await using var command = SqliteStoreSupport.CreateCommand(connection, transaction, sql);
            var index = 0;
            foreach (var id in chunk) command.Parameters.AddWithValue(parameters[index++], SqliteStoreSupport.Format(id));
            var found = new HashSet<Guid>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var id = Guid.Parse(reader.GetString(0));
                found.Add(id);
                rows[id] = new WorkflowGroupChildRunFact(id, ReadGuid(reader, 1), ReadEnum<WorkflowStatus>(reader.GetInt32(2)), true);
            }
            foreach (var id in chunk)
                if (!found.Contains(id)) rows[id] = new WorkflowGroupChildRunFact(id, null, null, false);
        }
        return Array.AsReadOnly(rows.Values.OrderBy(row => row.RunId).ToArray());
    }

    private static IReadOnlyList<WorkflowGroupStepFact> BuildStepFacts(Guid runId,
        IReadOnlyList<StepFactRow> rows, IReadOnlyList<WorkflowGroupWaitFact> waits)
    {
        var waitChildIds = waits.Where(wait => wait.ChildRunId is not null)
            .GroupBy(wait => wait.StepId).ToDictionary(group => group.Key, group => group.First().ChildRunId);
        return Array.AsReadOnly(rows.Select(row => new WorkflowGroupStepFact(runId, row.Id, row.StepKey, row.Revision, row.Attempt,
            row.Status, row.IsCurrent, row.GroupOccurrenceId, row.Role, row.LeaseGeneration, row.LeaseExpiresAt,
            row.AvailableAt, row.StartedAt, row.CompletedAt,
            row.ChildRunId ?? (waitChildIds.TryGetValue(row.Id, out var childId) ? childId : null))
        { InputHash = row.InputHash }).ToArray());
    }

    private static async ValueTask<WorkflowGroupGenerationFact?> ReadGenerationAsync(SqliteConnection connection,
        SqliteTransaction transaction, Guid runId, CancellationToken cancellationToken)
    {
        await using var command = SqliteStoreSupport.CreateCommand(connection, transaction, """
            SELECT generation_id,instance_id,ordinal,status,predecessor_generation_id FROM workflow_generations
            WHERE workflow_run_id=$run ORDER BY ordinal DESC LIMIT 1;
            """);
        command.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(runId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new WorkflowGroupGenerationFact(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)),
                reader.GetInt64(2), ReadEnum<WorkflowGenerationStatus>(reader.GetInt32(3)), ReadGuid(reader, 4))
            : null;
    }

    private static async ValueTask<IReadOnlyList<WorkflowGroupDispositionFact>> ReadDispositionsAsync(
        SqliteConnection connection, SqliteTransaction transaction, Guid generationId, CancellationToken cancellationToken)
    {
        await using var command = SqliteStoreSupport.CreateCommand(connection, transaction, """
            SELECT disposition_id,disposition,created_at FROM workflow_generation_dispositions
            WHERE generation_id=$generation ORDER BY rowid;
            """);
        command.Parameters.AddWithValue("$generation", SqliteStoreSupport.Format(generationId));
        var result = new List<WorkflowGroupDispositionFact>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(new WorkflowGroupDispositionFact(Guid.Parse(reader.GetString(0)),
                ReadEnum<CheckpointDisposition>(reader.GetInt32(1)), SqliteStoreSupport.ParseTimestamp(reader.GetString(2))));
        return result.AsReadOnly();
    }

    private static async ValueTask<WorkflowGroupOperationFact?> ReadOperationAsync(SqliteConnection connection,
        SqliteTransaction transaction, Guid runId, CancellationToken cancellationToken)
    {
        await using var command = SqliteStoreSupport.CreateCommand(connection, transaction, """
            SELECT operation_id,operation_type,status FROM workflow_run_operations WHERE workflow_run_id=$run
                AND status NOT IN ($completed,$failed) ORDER BY created_at,operation_id LIMIT 1;
            """);
        command.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(runId));
        command.Parameters.AddWithValue("$completed", (int)WorkflowOperationStatus.Completed);
        command.Parameters.AddWithValue("$failed", (int)WorkflowOperationStatus.Failed);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new WorkflowGroupOperationFact(Guid.Parse(reader.GetString(0)), reader.GetString(1),
                ReadEnum<WorkflowOperationStatus>(reader.GetInt32(2)))
            : null;
    }

    private static async ValueTask<long> ReadDurableWatermarkAsync(SqliteConnection connection,
        SqliteTransaction transaction, Guid runId, CancellationToken cancellationToken)
    {
        var advisory = WorkflowEventTypes.AdvisoryEventTypes;
        var predicate = advisory.Count == 0 ? "workflow_run_id=$run" : "workflow_run_id=$run AND " +
            string.Join(" AND ", advisory.Select((_, index) => $"event_type<>$adv{index}"));
        await using var command = SqliteStoreSupport.CreateCommand(connection, transaction,
            $"SELECT COALESCE(MAX(sequence),0) FROM workflow_events WHERE {predicate};");
        command.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(runId));
        var advisoryTypes = advisory.ToArray();
        for (var index = 0; index < advisoryTypes.Length; index++) command.Parameters.AddWithValue("$adv" + index, advisoryTypes[index]);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async ValueTask<long> ReadCreatedSequenceAsync(SqliteConnection connection,
        SqliteTransaction transaction, Guid runId, CancellationToken cancellationToken)
    {
        await using var command = SqliteStoreSupport.CreateCommand(connection, transaction, """
            SELECT COALESCE(MIN(sequence),0) FROM workflow_events WHERE workflow_run_id=$run AND event_type=$started;
            """);
        command.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(runId));
        command.Parameters.AddWithValue("$started", WorkflowEventTypes.WorkflowStarted);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static Guid? ReadGuid(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : Guid.Parse(reader.GetString(ordinal));
    private static string? ReadNullableString(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    private static T ReadEnum<T>(int value) where T : struct, Enum =>
        Enum.IsDefined((T)Enum.ToObject(typeof(T), value)) ? (T)Enum.ToObject(typeof(T), value) :
            throw new WorkflowStateException($"Stored {typeof(T).Name} value '{value}' is not supported.");

    private sealed record OccurrenceRow(string Id, string GroupKey, string? ParentId, string Coordinate, string ManifestDigest);
    private sealed record StepFactRow(Guid Id, string StepKey, int Revision, int Attempt, StepStatus Status,
        long LeaseGeneration, DateTimeOffset? LeaseExpiresAt, DateTimeOffset? AvailableAt, DateTimeOffset? StartedAt,
        DateTimeOffset? CompletedAt, string? InputHash, Guid? ChildRunId, string? GroupOccurrenceId,
        ActivityGroupMemberRole? Role, bool IsCurrent = false);
    private sealed record FanOutItemRow(WorkflowGroupFanOutItemFact Fact, string InputHash);
    private sealed record ExpansionHeader(string ExpansionId, string FanOutKey, Guid BoundaryStepId,
        string BoundaryStepKey, int BoundaryRevision, string PlanDigest, int ItemCount, string CanonicalJson);
}
