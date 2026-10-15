using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Penghou.Zhinu.Sqlite.Persistence.Groups;

internal sealed class SqliteWorkflowGroupRepository
{
    private readonly IZhinuSqliteDatabase factory;

    public SqliteWorkflowGroupRepository(IZhinuSqliteDatabase factory) => this.factory = factory;

    public async ValueTask<ActivityGroupManifest?> GetGroupManifestAsync(
        Guid workflowRunId,
        CancellationToken cancellationToken = default)
    {
        if (workflowRunId == Guid.Empty)
            throw new ArgumentException("Workflow ID must not be empty.", nameof(workflowRunId));
        await factory.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = SqliteStoreSupport.CreateCommand(connection, null, """
            SELECT m.contract_version, m.manifest_digest,
                   length(m.canonical_json), substr(m.canonical_json, 1, $maximum),
                   r.group_manifest_digest
            FROM workflow_group_manifests m
            JOIN workflow_runs r ON r.id = m.workflow_run_id
            WHERE m.workflow_run_id = $run;
            """);
        command.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(workflowRunId));
        command.Parameters.AddWithValue("$maximum", ActivityGroupManifestLimits.MaximumAllowedCanonicalBytes + 1);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;
        if (reader.GetInt32(2) > ActivityGroupManifestLimits.MaximumAllowedCanonicalBytes)
            throw new WorkflowStateException("The durable group manifest exceeds its size limit.");
        var manifest = RestoreManifest(reader.GetInt32(0), reader.GetString(1), reader.GetString(3));
        if (!string.Equals(reader.GetString(4), manifest.ManifestDigest, StringComparison.Ordinal))
            throw new WorkflowStateException("The durable group manifest does not match the run's immutable pin.");
        return manifest;
    }

    public async ValueTask<ActivityGroupOccurrence?> GetGroupOccurrenceAsync(
        Guid workflowRunId,
        string groupOccurrenceId,
        CancellationToken cancellationToken = default)
    {
        if (workflowRunId == Guid.Empty)
            throw new ArgumentException("Workflow ID must not be empty.", nameof(workflowRunId));
        ArgumentException.ThrowIfNullOrWhiteSpace(groupOccurrenceId);
        if (groupOccurrenceId.Length != 64 || groupOccurrenceId.Any(character =>
                !(character is >= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new ArgumentException("Occurrence IDs must be lowercase SHA-256 values.", nameof(groupOccurrenceId));
        await factory.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var manifest = await ReadManifestAsync(connection, null, workflowRunId, cancellationToken).ConfigureAwait(false);
        if (manifest is null)
            return null;
        var pin = await ReadRunPinAsync(connection, null, workflowRunId, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(pin, manifest.ManifestDigest, StringComparison.Ordinal))
            throw new WorkflowStateException("The durable group manifest does not match the run's immutable pin.");
        return await ReadOccurrenceAsync(connection, null, workflowRunId, groupOccurrenceId,
            manifest, new HashSet<string>(StringComparer.Ordinal), cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ActivityGroupStepMembership?> GetStepGroupMembershipAsync(
        Guid stepId,
        CancellationToken cancellationToken = default)
    {
        if (stepId == Guid.Empty)
            throw new ArgumentException("Step ID must not be empty.", nameof(stepId));
        await factory.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = SqliteStoreSupport.CreateCommand(connection, null, """
            SELECT workflow_run_id, step_id, step_key, step_revision, group_occurrence_id, role
            FROM workflow_step_group_memberships
            WHERE step_id = $step;
            """);
        command.Parameters.AddWithValue("$step", SqliteStoreSupport.Format(stepId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;
        return new ActivityGroupStepMembership
        {
            WorkflowRunId = Guid.Parse(reader.GetString(0)),
            StepId = Guid.Parse(reader.GetString(1)),
            StepKey = reader.GetString(2),
            StepRevision = reader.GetInt32(3),
            GroupOccurrenceId = reader.GetString(4),
            Role = (ActivityGroupMemberRole)reader.GetInt32(5)
        };
    }

    internal static async ValueTask ValidateAndPersistClaimAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        StepClaimRequest request,
        WorkflowStepRun step,
        bool allowInsertMembership,
        CancellationToken cancellationToken)
    {
        var pin = await ReadRunPinAsync(connection, transaction, request.WorkflowRunId, cancellationToken)
            .ConfigureAwait(false);
        if (pin is null)
        {
            if (request.GroupManifestDigest is not null || request.Group is not null)
                throw new WorkflowStateException("The step claim carries group metadata for an ungrouped run.");
            await EnsureManifestAbsentAsync(connection, transaction, request.WorkflowRunId, cancellationToken)
                .ConfigureAwait(false);
            await EnsureMembershipAbsentAsync(connection, transaction, step.Id, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (!string.Equals(request.GroupManifestDigest, pin, StringComparison.Ordinal))
            throw new WorkflowStateException("The step claim group-manifest pin does not match the admitted run.");
        var manifest = await ReadManifestAsync(connection, transaction, request.WorkflowRunId, cancellationToken)
            .ConfigureAwait(false) ?? throw new WorkflowStateException("The run's pinned group manifest is missing.");
        if (!string.Equals(manifest.ManifestDigest, pin, StringComparison.Ordinal))
            throw new WorkflowStateException("The durable group manifest does not match the admitted run pin.");
        if (request.Group is null)
        {
            await EnsureMembershipAbsentAsync(connection, transaction, step.Id, cancellationToken).ConfigureAwait(false);
            return;
        }

        var binding = request.Group;
        if (binding.Occurrence.WorkflowRunId != request.WorkflowRunId ||
            !string.Equals(binding.Occurrence.ManifestDigest, pin, StringComparison.Ordinal) ||
            !string.Equals(binding.Membership.GroupKey, binding.Occurrence.GroupKey, StringComparison.Ordinal))
            throw new WorkflowStateException("The step group binding belongs to a different run, manifest, or group.");
        manifest.ValidateMembership(binding.Membership);
        if (binding.Membership.Role is not (ActivityGroupMemberRole.Work or ActivityGroupMemberRole.Control))
            throw new WorkflowStateException("The step group role is invalid.");
        var validated = ValidateOccurrenceChain(binding.Occurrence, manifest, request.WorkflowRunId);
        foreach (var occurrence in validated)
            await InsertOrCompareOccurrenceAsync(connection, transaction, occurrence, pin, cancellationToken)
                .ConfigureAwait(false);

        var existing = await ReadMembershipRowAsync(connection, transaction, step.Id, cancellationToken)
            .ConfigureAwait(false);
        var expected = new ActivityGroupStepMembership
        {
            WorkflowRunId = step.WorkflowRunId,
            StepId = step.Id,
            StepKey = step.StepKey,
            StepRevision = step.Revision,
            GroupOccurrenceId = binding.Occurrence.GroupOccurrenceId,
            Role = binding.Membership.Role
        };
        if (existing is not null)
        {
            if (existing != expected)
                throw new WorkflowStateException("The durable step revision has a different group membership.");
            return;
        }
        if (!allowInsertMembership)
            throw new WorkflowStateException("The durable step revision has no group membership to match this claim.");
        await using var insert = SqliteStoreSupport.CreateCommand(connection, transaction, """
            INSERT INTO workflow_step_group_memberships
                (workflow_run_id, step_id, step_key, step_revision, group_occurrence_id, role)
            VALUES ($run, $step, $key, $revision, $occurrence, $role);
            """);
        insert.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(expected.WorkflowRunId));
        insert.Parameters.AddWithValue("$step", SqliteStoreSupport.Format(expected.StepId));
        insert.Parameters.AddWithValue("$key", expected.StepKey);
        insert.Parameters.AddWithValue("$revision", expected.StepRevision);
        insert.Parameters.AddWithValue("$occurrence", expected.GroupOccurrenceId);
        insert.Parameters.AddWithValue("$role", (int)expected.Role);
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static async ValueTask CloneMembershipAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        WorkflowStepRun source,
        WorkflowStepRun destination,
        CancellationToken cancellationToken)
    {
        var existing = await ReadMembershipRowAsync(connection, transaction, source.Id, cancellationToken)
            .ConfigureAwait(false);
        if (existing is null)
            return;
        await using var insert = SqliteStoreSupport.CreateCommand(connection, transaction, """
            INSERT INTO workflow_step_group_memberships
                (workflow_run_id, step_id, step_key, step_revision, group_occurrence_id, role)
            VALUES ($run, $step, $key, $revision, $occurrence, $role);
            """);
        insert.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(destination.WorkflowRunId));
        insert.Parameters.AddWithValue("$step", SqliteStoreSupport.Format(destination.Id));
        insert.Parameters.AddWithValue("$key", destination.StepKey);
        insert.Parameters.AddWithValue("$revision", destination.Revision);
        insert.Parameters.AddWithValue("$occurrence", existing.GroupOccurrenceId);
        insert.Parameters.AddWithValue("$role", (int)existing.Role);
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static List<ActivityGroupOccurrence> ValidateOccurrenceChain(
        ActivityGroupOccurrence occurrence,
        ActivityGroupManifest manifest,
        Guid runId)
    {
        var chain = new List<ActivityGroupOccurrence>();
        var current = occurrence;
        while (true)
        {
            if (chain.Count == ActivityGroupManifestLimits.MaximumAllowedDepth ||
                current.WorkflowRunId != runId ||
                !string.Equals(current.ManifestDigest, manifest.ManifestDigest, StringComparison.Ordinal))
                throw new WorkflowStateException("The group occurrence chain is invalid or exceeds its depth limit.");
            var parent = current.Parent;
            var recreated = ActivityGroupOccurrence.Create(runId, manifest, current.GroupKey, parent,
                current.ExecutionCoordinate);
            if (!string.Equals(recreated.GroupOccurrenceId, current.GroupOccurrenceId, StringComparison.Ordinal))
                throw new WorkflowStateException("The group occurrence ID does not match its deterministic coordinate.");
            chain.Add(current);
            if (parent is null)
                break;
            current = parent;
        }
        chain.Reverse();
        return chain;
    }

    private static async ValueTask InsertOrCompareOccurrenceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ActivityGroupOccurrence occurrence,
        string digest,
        CancellationToken cancellationToken)
    {
        await using (var count = SqliteStoreSupport.CreateCommand(connection, transaction, """
            SELECT COUNT(*) FROM workflow_group_occurrences WHERE workflow_run_id = $run;
            """))
        {
            count.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(occurrence.WorkflowRunId));
            var occurrenceCount = Convert.ToInt32(await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
            if (occurrenceCount >= ActivityGroupOccurrence.MaximumAllowedOccurrencesPerRun)
            {
                await using var existingCheck = SqliteStoreSupport.CreateCommand(connection, transaction, """
                    SELECT 1 FROM workflow_group_occurrences
                    WHERE workflow_run_id = $run AND group_occurrence_id = $id;
                    """);
                existingCheck.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(occurrence.WorkflowRunId));
                existingCheck.Parameters.AddWithValue("$id", occurrence.GroupOccurrenceId);
                if (await existingCheck.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null)
                    throw new WorkflowStateException("The run exceeds the activity-group occurrence limit.");
            }
        }
        await using var command = SqliteStoreSupport.CreateCommand(connection, transaction, """
            INSERT INTO workflow_group_occurrences
                (workflow_run_id, group_occurrence_id, group_key, parent_group_occurrence_id,
                 execution_coordinate, manifest_digest)
            VALUES ($run, $id, $key, $parent, $coordinate, $digest)
            ON CONFLICT(workflow_run_id, group_occurrence_id) DO NOTHING;
            """);
        command.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(occurrence.WorkflowRunId));
        command.Parameters.AddWithValue("$id", occurrence.GroupOccurrenceId);
        command.Parameters.AddWithValue("$key", occurrence.GroupKey);
        command.Parameters.AddWithValue("$parent", SqliteStoreSupport.DbValue(occurrence.ParentGroupOccurrenceId));
        command.Parameters.AddWithValue("$coordinate", occurrence.ExecutionCoordinate);
        command.Parameters.AddWithValue("$digest", digest);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await using var verify = SqliteStoreSupport.CreateCommand(connection, transaction, """
            SELECT group_key, parent_group_occurrence_id, execution_coordinate, manifest_digest
            FROM workflow_group_occurrences
            WHERE workflow_run_id = $run AND group_occurrence_id = $id;
            """);
        verify.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(occurrence.WorkflowRunId));
        verify.Parameters.AddWithValue("$id", occurrence.GroupOccurrenceId);
        await using var reader = await verify.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ||
            !string.Equals(reader.GetString(0), occurrence.GroupKey, StringComparison.Ordinal) ||
            !string.Equals(reader.IsDBNull(1) ? null : reader.GetString(1), occurrence.ParentGroupOccurrenceId, StringComparison.Ordinal) ||
            !string.Equals(reader.GetString(2), occurrence.ExecutionCoordinate, StringComparison.Ordinal) ||
            !string.Equals(reader.GetString(3), digest, StringComparison.Ordinal))
            throw new WorkflowStateException("A deterministic group occurrence ID is already bound to different data.");
    }

    private static async ValueTask EnsureMembershipAbsentAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid stepId,
        CancellationToken cancellationToken)
    {
        var existing = await ReadMembershipRowAsync(connection, transaction, stepId, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
            throw new WorkflowStateException("The durable step revision is grouped but this claim is ungrouped.");
    }

    private static async ValueTask EnsureManifestAbsentAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid runId,
        CancellationToken cancellationToken)
    {
        await using var command = SqliteStoreSupport.CreateCommand(connection, transaction, """
            SELECT 1 FROM workflow_group_manifests WHERE workflow_run_id = $run;
            """);
        command.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(runId));
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null)
            throw new WorkflowStateException("The ungrouped run unexpectedly has a durable group manifest.");
    }

    private static async ValueTask<ActivityGroupStepMembership?> ReadMembershipRowAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid stepId,
        CancellationToken cancellationToken)
    {
        await using var command = SqliteStoreSupport.CreateCommand(connection, transaction, """
            SELECT workflow_run_id, step_id, step_key, step_revision, group_occurrence_id, role
            FROM workflow_step_group_memberships WHERE step_id = $step;
            """);
        command.Parameters.AddWithValue("$step", SqliteStoreSupport.Format(stepId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;
        return new ActivityGroupStepMembership
        {
            WorkflowRunId = Guid.Parse(reader.GetString(0)),
            StepId = Guid.Parse(reader.GetString(1)),
            StepKey = reader.GetString(2),
            StepRevision = reader.GetInt32(3),
            GroupOccurrenceId = reader.GetString(4),
            Role = (ActivityGroupMemberRole)reader.GetInt32(5)
        };
    }

    private static async ValueTask<string?> ReadRunPinAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid runId,
        CancellationToken cancellationToken)
    {
        await using var command = SqliteStoreSupport.CreateCommand(connection, transaction,
            "SELECT group_manifest_digest FROM workflow_runs WHERE id = $run;");
        command.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(runId));
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null or DBNull ? null : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async ValueTask<ActivityGroupManifest?> ReadManifestAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid runId,
        CancellationToken cancellationToken)
    {
        await using var command = SqliteStoreSupport.CreateCommand(connection, transaction, """
            SELECT contract_version, manifest_digest, length(canonical_json),
                   substr(canonical_json, 1, $maximum)
            FROM workflow_group_manifests WHERE workflow_run_id = $run;
            """);
        command.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(runId));
        command.Parameters.AddWithValue("$maximum", ActivityGroupManifestLimits.MaximumAllowedCanonicalBytes + 1);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;
        if (reader.GetInt32(2) > ActivityGroupManifestLimits.MaximumAllowedCanonicalBytes)
            throw new WorkflowStateException("The durable group manifest exceeds its size limit.");
        return RestoreManifest(reader.GetInt32(0), reader.GetString(1), reader.GetString(3));
    }

    private static async ValueTask<ActivityGroupOccurrence?> ReadOccurrenceAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid runId,
        string occurrenceId,
        ActivityGroupManifest manifest,
        HashSet<string> seen,
        CancellationToken cancellationToken)
    {
        if (!seen.Add(occurrenceId) || seen.Count > ActivityGroupManifestLimits.MaximumAllowedDepth)
            throw new WorkflowStateException("The stored group occurrence hierarchy is cyclic or too deep.");
        await using var command = SqliteStoreSupport.CreateCommand(connection, transaction, """
            SELECT group_key, parent_group_occurrence_id, execution_coordinate, manifest_digest
            FROM workflow_group_occurrences
            WHERE workflow_run_id = $run AND group_occurrence_id = $id;
            """);
        command.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(runId));
        command.Parameters.AddWithValue("$id", occurrenceId);
        string groupKey, coordinate, digest;
        string? parentId;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                return null;
            groupKey = reader.GetString(0);
            parentId = reader.IsDBNull(1) ? null : reader.GetString(1);
            coordinate = reader.GetString(2);
            digest = reader.GetString(3);
        }
        if (!string.Equals(digest, manifest.ManifestDigest, StringComparison.Ordinal))
            throw new WorkflowStateException("A stored group occurrence has a different manifest pin.");
        ActivityGroupOccurrence? parent = null;
        if (parentId is not null)
        {
            parent = await ReadOccurrenceAsync(connection, transaction, runId, parentId, manifest, seen, cancellationToken)
                .ConfigureAwait(false) ?? throw new WorkflowStateException("A stored group occurrence has a missing parent.");
        }
        var occurrence = ActivityGroupOccurrence.Create(runId, manifest, groupKey, parent, coordinate);
        if (!string.Equals(occurrence.GroupOccurrenceId, occurrenceId, StringComparison.Ordinal))
            throw new WorkflowStateException("A stored group occurrence ID does not match its canonical coordinate.");
        return occurrence;
    }

    private static ActivityGroupManifest RestoreManifest(int contractVersion, string digest, string canonicalJson)
    {
        if (canonicalJson.Length > ActivityGroupManifestLimits.MaximumAllowedCanonicalBytes ||
            System.Text.Encoding.UTF8.GetByteCount(canonicalJson) > ActivityGroupManifestLimits.MaximumAllowedCanonicalBytes)
            throw new WorkflowStateException("The durable group manifest exceeds its size limit.");
        if (contractVersion != ActivityGroupManifest.CurrentContractVersion)
            throw new WorkflowStateException("The durable group manifest contract version is unsupported.");
        try
        {
            using var document = JsonDocument.Parse(canonicalJson, new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            if (root.GetProperty("contractVersion").GetInt32() != contractVersion)
                throw new WorkflowStateException("The canonical group manifest version is inconsistent.");
            var definitions = new List<ActivityGroupDefinition>();
            foreach (var item in root.GetProperty("definitions").EnumerateArray())
            {
                var provenanceElement = item.GetProperty("provenance");
                ActivityGroupMacroProvenance? provenance = null;
                if (provenanceElement.ValueKind != JsonValueKind.Null)
                {
                    provenance = new ActivityGroupMacroProvenance(
                        provenanceElement.GetProperty("macroName").GetString()!,
                        provenanceElement.GetProperty("macroDefinitionFingerprint").GetString()!,
                        provenanceElement.GetProperty("invocationKey").GetString()!,
                        GetOptional(provenanceElement, "macroVersion"),
                        GetOptional(provenanceElement, "sourceReference"));
                }
                definitions.Add(new ActivityGroupDefinition(
                    item.GetProperty("groupKey").GetString()!,
                    Enum.Parse<ActivityGroupKind>(item.GetProperty("kind").GetString()!, ignoreCase: false),
                    item.GetProperty("displayName").GetString()!,
                    GetOptional(item, "parentGroupKey"),
                    item.GetProperty("childOrdinal").GetInt32(),
                    provenance));
            }
            var restored = ActivityGroupManifest.Create(definitions, contractVersion: contractVersion);
            if (!string.Equals(restored.CanonicalJson, canonicalJson, StringComparison.Ordinal) ||
                !string.Equals(restored.ManifestDigest, digest, StringComparison.Ordinal))
                throw new WorkflowStateException("The durable group manifest canonical bytes or digest do not match.");
            return restored;
        }
        catch (WorkflowStateException) { throw; }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or FormatException)
        {
            throw new WorkflowStateException($"The durable group manifest is malformed: {error.Message}");
        }
    }

    private static string? GetOptional(JsonElement element, string property) =>
        element.GetProperty(property).ValueKind == JsonValueKind.Null
            ? null
            : element.GetProperty(property).GetString();
}
