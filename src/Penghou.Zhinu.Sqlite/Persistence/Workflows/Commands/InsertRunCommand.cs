using Microsoft.Data.Sqlite;

namespace Penghou.Zhinu.Sqlite.Persistence.Workflows;

internal sealed class InsertRunCommand
{
    public async ValueTask ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        WorkflowRun run,
        CancellationToken cancellationToken)
    {
        await using var command = SqliteStoreSupport.CreateCommand(connection, transaction, """
            INSERT INTO workflow_runs
            (id, workflow_name, workflow_version, status, input_json,
             input_type, output_json, output_type, error_json, created_at,
             updated_at, completed_at, deadline, metadata_json, parent_run_id,
             source_run_id, trace_id, lease_owner, lease_expires_at,
             definition_fingerprint)
            VALUES
            ($id, $name, $version, $status, $inputJson,
             $inputType, $outputJson, $outputType, $errorJson, $createdAt,
             $updatedAt, $completedAt, $deadline, $metadataJson, $parentRunId,
             $sourceRunId, $traceId, $leaseOwner, $leaseExpiresAt,
             $definitionFingerprint);
            """);
        AddRunParameters(command, run);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AddRunParameters(SqliteCommand command, WorkflowRun run)
    {
        command.Parameters.AddWithValue("$id", SqliteStoreSupport.Format(run.Id));
        command.Parameters.AddWithValue("$name", run.WorkflowName);
        command.Parameters.AddWithValue("$version", run.WorkflowVersion);
        command.Parameters.AddWithValue("$status", (int)run.Status);
        command.Parameters.AddWithValue("$inputJson", SqliteStoreSupport.DbValue(run.InputJson));
        command.Parameters.AddWithValue("$inputType", SqliteStoreSupport.DbValue(run.InputType));
        command.Parameters.AddWithValue("$outputJson", SqliteStoreSupport.DbValue(run.OutputJson));
        command.Parameters.AddWithValue("$outputType", SqliteStoreSupport.DbValue(run.OutputType));
        command.Parameters.AddWithValue(
            "$errorJson",
            SqliteStoreSupport.DbValue(SqliteStoreSupport.SerializeError(run.Error)));
        command.Parameters.AddWithValue("$createdAt", SqliteStoreSupport.FormatTimestamp(run.CreatedAt));
        command.Parameters.AddWithValue("$updatedAt", SqliteStoreSupport.FormatTimestamp(run.UpdatedAt));
        command.Parameters.AddWithValue(
            "$completedAt",
            SqliteStoreSupport.DbValue(SqliteStoreSupport.FormatNullable(run.CompletedAt)));
        command.Parameters.AddWithValue(
            "$deadline",
            SqliteStoreSupport.DbValue(SqliteStoreSupport.FormatNullable(run.Deadline)));
        command.Parameters.AddWithValue("$metadataJson", SqliteStoreSupport.DbValue(run.MetadataJson));
        command.Parameters.AddWithValue(
            "$parentRunId",
            run.ParentRunId is { } parentId ? SqliteStoreSupport.Format(parentId) : DBNull.Value);
        command.Parameters.AddWithValue(
            "$sourceRunId",
            run.SourceRunId is { } sourceId ? SqliteStoreSupport.Format(sourceId) : DBNull.Value);
        command.Parameters.AddWithValue("$traceId", SqliteStoreSupport.DbValue(run.TraceId));
        command.Parameters.AddWithValue("$leaseOwner", SqliteStoreSupport.DbValue(run.LeaseOwner));
        command.Parameters.AddWithValue(
            "$leaseExpiresAt",
            SqliteStoreSupport.DbValue(SqliteStoreSupport.FormatNullable(run.LeaseExpiresAt)));
        command.Parameters.AddWithValue(
            "$definitionFingerprint",
            SqliteStoreSupport.DbValue(run.DefinitionFingerprint));
    }
}
