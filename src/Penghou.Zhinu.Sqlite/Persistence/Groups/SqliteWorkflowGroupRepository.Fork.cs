using Microsoft.Data.Sqlite;

namespace Penghou.Zhinu.Sqlite.Persistence.Groups;

internal sealed partial class SqliteWorkflowGroupRepository
{
    internal static async ValueTask<ForkCopyContext> PrepareForkAsync(
        SqliteConnection connection, SqliteTransaction transaction,
        WorkflowRun source, WorkflowRun destination, ActivityGroupManifest? targetManifest,
        CancellationToken cancellationToken)
    {
        await using (var retirement = SqliteStoreSupport.CreateCommand(connection, transaction,
            "SELECT 1 FROM workflow_group_fanout_retirements WHERE workflow_run_id=$run LIMIT 1;"))
        {
            retirement.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(source.Id));
            if (await retirement.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null)
                throw new WorkflowStateException("Forking a run with explicit branch retirement is not qualified by this profile.");
        }
        if (destination.GroupManifestDigest != targetManifest?.ManifestDigest)
            throw new WorkflowStateException("The fork destination pin does not match its supplied group manifest.");
        if (source.AuthorizationProviderId != destination.AuthorizationProviderId ||
            source.AuthorizationBindingId != destination.AuthorizationBindingId ||
            source.AuthoritySubjectBinding != destination.AuthoritySubjectBinding)
            throw new WorkflowStateException("A grouped fork must preserve the source authority binding.");

        ActivityGroupManifest? sourceManifest = null;
        if (source.GroupManifestDigest is null)
            await EnsureManifestAbsentAsync(connection, transaction, source.Id, cancellationToken).ConfigureAwait(false);
        else
        {
            sourceManifest = await ReadManifestAsync(connection, transaction, source.Id, cancellationToken)
                .ConfigureAwait(false);
            if (sourceManifest is null || sourceManifest.ManifestDigest != source.GroupManifestDigest)
                throw new WorkflowStateException("The fork source's durable group manifest is missing or inconsistent.");
        }
        return new ForkCopyContext(source.Id, destination.Id, sourceManifest, targetManifest);
    }

    internal sealed class ForkCopyContext(
        Guid sourceRunId, Guid destinationRunId,
        ActivityGroupManifest? sourceManifest, ActivityGroupManifest? targetManifest)
    {
        private readonly Dictionary<string, ActivityGroupOccurrence> rebound = new(StringComparer.Ordinal);

        internal async ValueTask InstallManifestAsync(
            SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
        {
            if (targetManifest is null) return;
            await using var command = SqliteStoreSupport.CreateCommand(connection, transaction, """
                INSERT INTO workflow_group_manifests
                    (workflow_run_id, contract_version, manifest_digest, canonical_json)
                VALUES ($run, $version, $digest, $canonical);
                """);
            command.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(destinationRunId));
            command.Parameters.AddWithValue("$version", targetManifest.ContractVersion);
            command.Parameters.AddWithValue("$digest", targetManifest.ManifestDigest);
            command.Parameters.AddWithValue("$canonical", targetManifest.CanonicalJson);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        internal async ValueTask CopyMembershipAsync(
            SqliteConnection connection, SqliteTransaction transaction,
            WorkflowStepRun sourceStep, WorkflowStepRun destinationStep, CancellationToken cancellationToken)
        {
            var membership = await ReadMembershipRowAsync(connection, transaction, sourceStep.Id, cancellationToken)
                .ConfigureAwait(false);
            if (membership is null) return; // Null membership is an immutable fact, including in a grouped destination.
            if (sourceManifest is null || targetManifest is null ||
                membership.WorkflowRunId != sourceRunId || membership.StepId != sourceStep.Id ||
                membership.StepKey != sourceStep.StepKey || membership.StepRevision != sourceStep.Revision ||
                membership.Role is not (ActivityGroupMemberRole.Work or ActivityGroupMemberRole.Control))
                throw new WorkflowStateException("A copied group membership has no compatible source or destination manifest.");

            if (!rebound.TryGetValue(membership.GroupOccurrenceId, out var occurrence))
            {
                var sourceOccurrence = await ReadOccurrenceAsync(connection, transaction, sourceRunId,
                    membership.GroupOccurrenceId, sourceManifest, new HashSet<string>(StringComparer.Ordinal), cancellationToken)
                    .ConfigureAwait(false) ?? throw new WorkflowStateException("A copied membership's source occurrence is missing.");
                occurrence = await RebindAsync(connection, transaction, sourceOccurrence, cancellationToken).ConfigureAwait(false);
            }
            await using var insert = SqliteStoreSupport.CreateCommand(connection, transaction, """
                INSERT INTO workflow_step_group_memberships
                    (workflow_run_id, step_id, step_key, step_revision, group_occurrence_id, role)
                VALUES ($run, $step, $key, $revision, $occurrence, $role);
                """);
            insert.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(destinationRunId));
            insert.Parameters.AddWithValue("$step", SqliteStoreSupport.Format(destinationStep.Id));
            insert.Parameters.AddWithValue("$key", destinationStep.StepKey);
            insert.Parameters.AddWithValue("$revision", destinationStep.Revision);
            insert.Parameters.AddWithValue("$occurrence", occurrence.GroupOccurrenceId);
            insert.Parameters.AddWithValue("$role", (int)membership.Role);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        private async ValueTask<ActivityGroupOccurrence> RebindAsync(
            SqliteConnection connection, SqliteTransaction transaction,
            ActivityGroupOccurrence source, CancellationToken cancellationToken)
        {
            if (rebound.TryGetValue(source.GroupOccurrenceId, out var existing)) return existing;
            // Fork lineage is already the destination RunId. Reuse the existing
            // coordinate and hash domain; do not invent a fork frame or parse keys.
            if (source.ExecutionCoordinate.StartsWith('{'))
                throw new WorkflowStateException("Fan-out occurrence copying requires a qualified expansion-aware fork profile.");
            var definition = sourceManifest!.FindDefinition(source.GroupKey)!;
            var target = targetManifest!.FindDefinition(source.GroupKey);
            if (target is null || target.Kind != definition.Kind || target.ParentGroupKey != definition.ParentGroupKey ||
                !SameProvenance(definition.Provenance, target.Provenance))
                throw new WorkflowStateException("A reused group must retain its definition key, kind, parent lineage and macro provenance.");
            var parent = source.Parent is null ? null :
                await RebindAsync(connection, transaction, source.Parent, cancellationToken).ConfigureAwait(false);
            var destination = ActivityGroupOccurrence.Create(destinationRunId, targetManifest, source.GroupKey, parent,
                source.ExecutionCoordinate);
            await InsertOrCompareOccurrenceAsync(connection, transaction, destination, targetManifest.ManifestDigest,
                cancellationToken).ConfigureAwait(false);
            rebound.Add(source.GroupOccurrenceId, destination);
            return destination;
        }

        private static bool SameProvenance(ActivityGroupMacroProvenance? left, ActivityGroupMacroProvenance? right) =>
            left is null ? right is null : right is not null &&
            left.MacroName == right.MacroName && left.MacroDefinitionFingerprint == right.MacroDefinitionFingerprint &&
            left.InvocationKey == right.InvocationKey && left.MacroVersion == right.MacroVersion &&
            left.SourceReference == right.SourceReference;
    }
}
