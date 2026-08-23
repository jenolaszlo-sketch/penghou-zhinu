using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Text.Json;

namespace Penghou.Zhinu.Sqlite.Persistence;

/// <summary>
/// Shared SQLite command creation, value formatting, serialization, and
/// row-reading helpers used by the persistence commands, queries, and
/// repositories.
/// </summary>
internal static class SqliteStoreSupport
{
    internal static readonly JsonSerializerOptions SerializerOptions =
        Penghou.Zhinu.ZhinuJsonDefaults.CreateDefault();

    internal static SqliteCommand CreateCommand(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string text)
    {
        var command = connection.CreateCommand();
        command.CommandText = text;
        command.Transaction = transaction;
        return command;
    }

    internal static async ValueTask ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string text,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction, text);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static string Format(Guid value) => value.ToString("D");

    internal static string FormatTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    internal static string? FormatNullable(DateTimeOffset? value) =>
        value is null ? null : FormatTimestamp(value.Value);

    internal static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.ParseExact(
            value,
            "O",
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);

    internal static DateTimeOffset? ParseNullableTimestamp(
        SqliteDataReader reader,
        int ordinal) =>
        reader.IsDBNull(ordinal)
            ? null
            : ParseTimestamp(reader.GetString(ordinal));

    internal static string? GetNullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    internal static object DbValue(string? value) => value ?? (object)DBNull.Value;

    internal static string? SerializeError(WorkflowError? error) =>
        error is null ? null : JsonSerializer.Serialize(error, SerializerOptions);

    internal static WorkflowError? DeserializeError(string? json) =>
        json is null
            ? null
            : JsonSerializer.Deserialize<WorkflowError>(json, SerializerOptions);

    internal static WorkflowRun ReadRun(SqliteDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(0)),
        WorkflowName = reader.GetString(1),
        WorkflowVersion = reader.GetString(2),
        Status = (WorkflowStatus)reader.GetInt32(3),
        InputJson = GetNullableString(reader, 4),
        InputType = GetNullableString(reader, 5),
        OutputJson = GetNullableString(reader, 6),
        OutputType = GetNullableString(reader, 7),
        Error = DeserializeError(GetNullableString(reader, 8)),
        CreatedAt = ParseTimestamp(reader.GetString(9)),
        UpdatedAt = ParseTimestamp(reader.GetString(10)),
        CompletedAt = ParseNullableTimestamp(reader, 11),
        Deadline = ParseNullableTimestamp(reader, 12),
        MetadataJson = GetNullableString(reader, 13),
        ParentRunId = reader.IsDBNull(14) ? null : Guid.Parse(reader.GetString(14)),
        SourceRunId = reader.IsDBNull(15) ? null : Guid.Parse(reader.GetString(15)),
        TraceId = GetNullableString(reader, 16),
        LeaseOwner = GetNullableString(reader, 17),
        LeaseExpiresAt = ParseNullableTimestamp(reader, 18),
        LeaseGeneration = reader.GetInt64(19),
        DefinitionFingerprint = GetNullableString(reader, 20)
    };

    internal static WorkflowStepRun ReadStep(SqliteDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(0)),
        WorkflowRunId = Guid.Parse(reader.GetString(1)),
        StepKey = reader.GetString(2),
        Status = (StepStatus)reader.GetInt32(3),
        Attempt = reader.GetInt32(4),
        InputJson = GetNullableString(reader, 5),
        InputType = GetNullableString(reader, 6),
        InputHash = GetNullableString(reader, 7),
        OutputJson = GetNullableString(reader, 8),
        OutputType = GetNullableString(reader, 9),
        Error = DeserializeError(GetNullableString(reader, 10)),
        SignalName = GetNullableString(reader, 11),
        CreatedAt = ParseTimestamp(reader.GetString(12)),
        StartedAt = ParseNullableTimestamp(reader, 13),
        CompletedAt = ParseNullableTimestamp(reader, 14),
        AvailableAt = ParseNullableTimestamp(reader, 15),
        LeaseOwner = GetNullableString(reader, 16),
        LeaseExpiresAt = ParseNullableTimestamp(reader, 17),
        Revision = reader.GetInt32(18),
        LeaseGeneration = reader.GetInt64(19)
    };

    internal static WorkflowStepCompensation ReadCompensation(SqliteDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(0)),
        WorkflowRunId = Guid.Parse(reader.GetString(1)),
        StepKey = reader.GetString(2),
        Revision = reader.GetInt32(3),
        CompensationName = reader.GetString(4),
        Status = (CompensationStatus)reader.GetInt32(5),
        Attempt = reader.GetInt32(6),
        InputJson = GetNullableString(reader, 7),
        InputType = GetNullableString(reader, 8),
        OutputJson = GetNullableString(reader, 9),
        Error = DeserializeError(GetNullableString(reader, 10)),
        RetryPolicyJson = GetNullableString(reader, 11),
        ExecutionTimeout = reader.IsDBNull(12)
            ? null
            : TimeSpan.FromTicks(reader.GetInt64(12)),
        AvailableAt = ParseNullableTimestamp(reader, 13),
        TimeoutAt = ParseNullableTimestamp(reader, 14),
        LeaseOwner = GetNullableString(reader, 15),
        LeaseExpiresAt = ParseNullableTimestamp(reader, 16),
        LeaseGeneration = reader.GetInt64(17),
        StartedAt = ParseNullableTimestamp(reader, 18),
        CompletedAt = ParseNullableTimestamp(reader, 19),
        CreatedAt = ParseTimestamp(reader.GetString(20)),
        Actor = GetNullableString(reader, 21),
        Reason = GetNullableString(reader, 22),
        IdempotencyKey = GetNullableString(reader, 23)
    };

    internal static WorkflowRunOperation ReadOperation(SqliteDataReader reader) => new()
    {
        OperationId = Guid.Parse(reader.GetString(0)),
        WorkflowRunId = Guid.Parse(reader.GetString(1)),
        OperationType = reader.GetString(2),
        Status = (WorkflowOperationStatus)reader.GetInt32(3),
        PayloadJson = GetNullableString(reader, 4),
        CreatedAt = ParseTimestamp(reader.GetString(5)),
        UpdatedAt = ParseTimestamp(reader.GetString(6)),
        CompletedAt = ParseNullableTimestamp(reader, 7)
    };

    internal static async ValueTask<IReadOnlyList<WorkflowStepRun>> ReadStepsAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        var results = new List<WorkflowStepRun>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            results.Add(ReadStep(reader));
        return results;
    }

    internal const string RunColumns = """
        id, workflow_name, workflow_version, status, input_json, input_type,
        output_json, output_type, error_json, created_at, updated_at,
        completed_at, deadline, metadata_json, parent_run_id, source_run_id,
        trace_id, lease_owner, lease_expires_at, lease_generation,
        definition_fingerprint
        """;

    internal const string StepColumns = """
        id, workflow_run_id, step_key, status, attempt, input_json, input_type,
        input_hash, output_json, output_type, error_json, signal_name, created_at,
        started_at, completed_at, available_at, lease_owner, lease_expires_at,
        revision, lease_generation
        """;

    internal const string CompensationColumns = """
        id, workflow_run_id, step_key, revision, compensation_name, status,
        attempt, input_json, input_type, output_json, error_json,
        retry_policy_json, timeout_ticks, available_at, timeout_at,
        lease_owner, lease_expires_at, lease_generation, started_at,
        completed_at, created_at, actor, reason, idempotency_key
        """;

    internal const string OperationColumns = """
        operation_id, workflow_run_id, operation_type, status, payload_json,
        created_at, updated_at, completed_at
        """;

    private static JsonSerializerOptions CreateSerializerOptions() =>
        Penghou.Zhinu.ZhinuJsonDefaults.CreateDefault();
}
