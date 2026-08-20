using System.Text.Json;
using System.Collections.ObjectModel;
using Microsoft.Data.Sqlite;

namespace Penghou.Zhinu.Sqlite.Persistence.Artifacts;

internal sealed class SqliteArtifactRepository(SqliteConnectionFactory factory) :
    IWorkflowArtifactRepository
{
    private const string Columns = """
        id, workflow_run_id, name, revision, artifact_type, artifact_version,
        location, content_hash, metadata_json, producer_step_key,
        producer_step_revision, created_at
        """;

    public async ValueTask<WorkflowArtifactReference> PublishArtifactAsync(
        ArtifactPublicationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate(request);
        await factory.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await factory.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);

        if (request.StepExecutionId is { } stepId)
            await VerifyProducerAsync(connection, transaction, request, stepId, cancellationToken)
                .ConfigureAwait(false);

        var metadataJson = SerializeMetadata(request.Artifact.Metadata);
        var existing = await GetInPublicationScopeAsync(
            connection, transaction, request, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            if (Equivalent(existing, request.Artifact, metadataJson))
                return existing;
            throw new WorkflowStateException(
                $"Artifact '{request.Artifact.Name}' was already published with different " +
                "data in this execution scope.");
        }

        var revision = await GetNextRevisionAsync(
            connection, transaction, request.WorkflowRunId, request.Artifact.Name,
            cancellationToken).ConfigureAwait(false);
        var result = new WorkflowArtifactReference
        {
            Id = Guid.NewGuid(),
            WorkflowRunId = request.WorkflowRunId,
            Name = request.Artifact.Name,
            Revision = revision,
            ArtifactType = request.Artifact.ArtifactType,
            ArtifactVersion = request.Artifact.ArtifactVersion,
            Location = request.Artifact.Location,
            ContentHash = request.Artifact.ContentHash,
            Metadata = SnapshotMetadata(request.Artifact.Metadata),
            ProducerStepKey = request.ProducerStepKey,
            ProducerStepRevision = request.ProducerStepRevision,
            CreatedAt = request.Now
        };
        await InsertAsync(connection, transaction, result, metadataJson, cancellationToken)
            .ConfigureAwait(false);
        transaction.Commit();
        return result;
    }

    public async ValueTask<WorkflowArtifactReference?> GetArtifactAsync(
        Guid artifactId,
        CancellationToken cancellationToken = default)
    {
        await factory.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await factory.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = SqliteStoreSupport.CreateCommand(
            connection, null, $"SELECT {Columns} FROM workflow_artifacts WHERE id = $id;");
        command.Parameters.AddWithValue("$id", SqliteStoreSupport.Format(artifactId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? Read(reader)
            : null;
    }

    public async ValueTask<IReadOnlyList<WorkflowArtifactReference>> GetArtifactsAsync(
        Guid workflowRunId,
        CancellationToken cancellationToken = default)
    {
        await factory.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await factory.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = SqliteStoreSupport.CreateCommand(
            connection, null,
            $"SELECT {Columns} FROM workflow_artifacts " +
            "WHERE workflow_run_id = $run ORDER BY created_at, name, revision;");
        command.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(workflowRunId));
        var results = new List<WorkflowArtifactReference>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            results.Add(Read(reader));
        return results;
    }

    private static async ValueTask VerifyProducerAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ArtifactPublicationRequest request,
        Guid stepId,
        CancellationToken cancellationToken)
    {
        await using var command = SqliteStoreSupport.CreateCommand(connection, transaction, """
            SELECT COUNT(*) FROM workflow_steps
            WHERE id = $id AND workflow_run_id = $run AND step_key = $key
                AND revision = $revision;
            """);
        command.Parameters.AddWithValue("$id", SqliteStoreSupport.Format(stepId));
        command.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(request.WorkflowRunId));
        command.Parameters.AddWithValue("$key", request.ProducerStepKey!);
        command.Parameters.AddWithValue("$revision", request.ProducerStepRevision!.Value);
        var count = Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
        if (count != 1)
            throw new WorkflowStateException("The artifact producer step revision does not exist.");
    }

    private static async ValueTask<WorkflowArtifactReference?> GetInPublicationScopeAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ArtifactPublicationRequest request,
        CancellationToken cancellationToken)
    {
        var scope = request.StepExecutionId is null
            ? "producer_step_key IS NULL"
            : "producer_step_key = $key AND producer_step_revision = $stepRevision";
        await using var command = SqliteStoreSupport.CreateCommand(
            connection, transaction,
            $"SELECT {Columns} FROM workflow_artifacts WHERE workflow_run_id = $run " +
            $"AND name = $name AND {scope} ORDER BY revision DESC LIMIT 1;");
        command.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(request.WorkflowRunId));
        command.Parameters.AddWithValue("$name", request.Artifact.Name);
        if (request.StepExecutionId is not null)
        {
            command.Parameters.AddWithValue("$key", request.ProducerStepKey!);
            command.Parameters.AddWithValue("$stepRevision", request.ProducerStepRevision!.Value);
        }
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? Read(reader)
            : null;
    }

    private static async ValueTask<int> GetNextRevisionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid runId,
        string name,
        CancellationToken cancellationToken)
    {
        await using var command = SqliteStoreSupport.CreateCommand(connection, transaction, """
            SELECT COALESCE(MAX(revision), 0) + 1 FROM workflow_artifacts
            WHERE workflow_run_id = $run AND name = $name;
            """);
        command.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(runId));
        command.Parameters.AddWithValue("$name", name);
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
    }

    private static async ValueTask InsertAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        WorkflowArtifactReference artifact,
        string? metadataJson,
        CancellationToken cancellationToken)
    {
        await using var command = SqliteStoreSupport.CreateCommand(connection, transaction, """
            INSERT INTO workflow_artifacts
                (id, workflow_run_id, name, revision, artifact_type, artifact_version,
                 location, content_hash, metadata_json, producer_step_key,
                 producer_step_revision, created_at)
            VALUES
                ($id, $run, $name, $revision, $type, $version, $location, $hash,
                 $metadata, $stepKey, $stepRevision, $createdAt);
            """);
        command.Parameters.AddWithValue("$id", SqliteStoreSupport.Format(artifact.Id));
        command.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(artifact.WorkflowRunId));
        command.Parameters.AddWithValue("$name", artifact.Name);
        command.Parameters.AddWithValue("$revision", artifact.Revision);
        command.Parameters.AddWithValue("$type", artifact.ArtifactType);
        command.Parameters.AddWithValue("$version", SqliteStoreSupport.DbValue(artifact.ArtifactVersion));
        command.Parameters.AddWithValue("$location", artifact.Location);
        command.Parameters.AddWithValue("$hash", SqliteStoreSupport.DbValue(artifact.ContentHash));
        command.Parameters.AddWithValue("$metadata", SqliteStoreSupport.DbValue(metadataJson));
        command.Parameters.AddWithValue("$stepKey", SqliteStoreSupport.DbValue(artifact.ProducerStepKey));
        command.Parameters.AddWithValue("$stepRevision", artifact.ProducerStepRevision ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$createdAt", SqliteStoreSupport.FormatTimestamp(artifact.CreatedAt));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static WorkflowArtifactReference Read(SqliteDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(0)),
        WorkflowRunId = Guid.Parse(reader.GetString(1)),
        Name = reader.GetString(2),
        Revision = reader.GetInt32(3),
        ArtifactType = reader.GetString(4),
        ArtifactVersion = SqliteStoreSupport.GetNullableString(reader, 5),
        Location = reader.GetString(6),
        ContentHash = SqliteStoreSupport.GetNullableString(reader, 7),
        Metadata = DeserializeMetadata(SqliteStoreSupport.GetNullableString(reader, 8)),
        ProducerStepKey = SqliteStoreSupport.GetNullableString(reader, 9),
        ProducerStepRevision = reader.IsDBNull(10) ? null : reader.GetInt32(10),
        CreatedAt = SqliteStoreSupport.ParseTimestamp(reader.GetString(11))
    };

    private static bool Equivalent(
        WorkflowArtifactReference existing,
        WorkflowArtifactDescriptor artifact,
        string? metadataJson) =>
        existing.ArtifactType == artifact.ArtifactType &&
        existing.ArtifactVersion == artifact.ArtifactVersion &&
        existing.Location == artifact.Location &&
        existing.ContentHash == artifact.ContentHash &&
        SerializeMetadata(existing.Metadata) == metadataJson;

    private static string? SerializeMetadata(IReadOnlyDictionary<string, string>? metadata) =>
        metadata is null
            ? null
            : JsonSerializer.Serialize(
                new SortedDictionary<string, string>(
                    metadata.ToDictionary(pair => pair.Key, pair => pair.Value),
                    StringComparer.Ordinal),
                SqliteStoreSupport.SerializerOptions);

    private static IReadOnlyDictionary<string, string>? DeserializeMetadata(string? json) =>
        json is null
            ? null
            : SnapshotMetadata(
                JsonSerializer.Deserialize<Dictionary<string, string>>(
                    json, SqliteStoreSupport.SerializerOptions));

    private static IReadOnlyDictionary<string, string>? SnapshotMetadata(
        IReadOnlyDictionary<string, string>? metadata) =>
        metadata is null
            ? null
            : new ReadOnlyDictionary<string, string>(
                metadata.ToDictionary(pair => pair.Key, pair => pair.Value));

    private static void Validate(ArtifactPublicationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request.Artifact);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Artifact.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Artifact.ArtifactType);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Artifact.Location);
        var hasStepId = request.StepExecutionId is not null;
        var hasStepKey = request.ProducerStepKey is not null;
        var hasStepRevision = request.ProducerStepRevision is not null;
        if (hasStepId != hasStepKey || hasStepId != hasStepRevision)
        {
            throw new ArgumentException(
                "Step execution id, key, and revision must be provided together.",
                nameof(request));
        }
    }
}
