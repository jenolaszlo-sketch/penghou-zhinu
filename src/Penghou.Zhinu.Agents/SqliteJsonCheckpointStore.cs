using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using Microsoft.Data.Sqlite;
using Penghou.Zhinu.Sqlite;
using System.Globalization;
using System.Text.Json;

namespace Penghou.Zhinu.Agents;

/// <summary>
/// A <see cref="JsonCheckpointStore"/> that persists Microsoft Agent Framework
/// workflow checkpoints in the same SQLite database as the Zhinu workflow runs.
/// Instances are thread-safe and may be shared across runs and processes, unlike
/// the file-based store shipped with MAF.
/// </summary>
public sealed class SqliteJsonCheckpointStore : JsonCheckpointStore
{
    private const string Schema = """
        CREATE TABLE IF NOT EXISTS maf_checkpoints
        (
            session_id TEXT NOT NULL,
            checkpoint_id TEXT NOT NULL,
            parent_checkpoint_id TEXT NULL,
            data_json TEXT NOT NULL,
            created_at TEXT NOT NULL,
            PRIMARY KEY (session_id, checkpoint_id)
        );

        CREATE INDEX IF NOT EXISTS ix_maf_checkpoints_session_parent
            ON maf_checkpoints(session_id, parent_checkpoint_id);
        """;

    private readonly ZhinuSqliteOptions options;
    private readonly string connectionString;
    private readonly SemaphoreSlim initializationLock = new(1, 1);
    private volatile bool initialized;

    public SqliteJsonCheckpointStore(ZhinuSqliteOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.DatabasePath);
        if (options.BusyTimeout < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options));
        this.options = options;
        var path = Path.GetFullPath(options.DatabasePath);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        }.ToString();
    }

    /// <summary>
    /// Persists a new checkpoint for <paramref name="sessionId"/> and returns
    /// its identifier. The returned <see cref="CheckpointInfo"/> can later be
    /// retrieved or used as the parent of a subsequent checkpoint.
    /// </summary>
    public override async ValueTask<CheckpointInfo> CreateCheckpointAsync(
        string sessionId,
        JsonElement value,
        CheckpointInfo? parent = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var checkpointId = Guid.NewGuid().ToString("N");
        await EnsureInitializedAsync().ConfigureAwait(false);
        await using var connection = await OpenAsync().ConfigureAwait(false);
        await using var command = CreateCommand(connection, """
            INSERT INTO maf_checkpoints
                (session_id, checkpoint_id, parent_checkpoint_id, data_json, created_at)
            VALUES
                ($session_id, $checkpoint_id, $parent_checkpoint_id, $data_json, $created_at);
            """);
        command.Parameters.AddWithValue("$session_id", sessionId);
        command.Parameters.AddWithValue("$checkpoint_id", checkpointId);
        command.Parameters.AddWithValue(
            "$parent_checkpoint_id",
            DbValue(parent?.CheckpointId));
        command.Parameters.AddWithValue("$data_json", value.GetRawText());
        command.Parameters.AddWithValue("$created_at", FormatTimestamp(DateTimeOffset.UtcNow));
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        return new CheckpointInfo(sessionId, checkpointId);
    }

    /// <summary>Retrieves a previously persisted checkpoint payload.</summary>
    public override async ValueTask<JsonElement> RetrieveCheckpointAsync(
        string sessionId,
        CheckpointInfo key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(key);
        await EnsureInitializedAsync().ConfigureAwait(false);
        await using var connection = await OpenAsync().ConfigureAwait(false);
        await using var command = CreateCommand(connection, """
            SELECT data_json FROM maf_checkpoints
            WHERE session_id = $session_id AND checkpoint_id = $checkpoint_id;
            """);
        command.Parameters.AddWithValue("$session_id", sessionId);
        command.Parameters.AddWithValue("$checkpoint_id", key.CheckpointId);
        var data = (string?)await command.ExecuteScalarAsync().ConfigureAwait(false);
        if (data is null)
        {
            throw new KeyNotFoundException(
                $"Checkpoint '{key.CheckpointId}' not found for session '{sessionId}'.");
        }
        return JsonDocument.Parse(data).RootElement.Clone();
    }

    /// <summary>
    /// Lists the checkpoints for <paramref name="sessionId"/>, most recently
    /// created first, optionally filtered to children of
    /// <paramref name="withParent"/>. The ordering makes the first element the
    /// natural resume point for a workflow.
    /// </summary>
    public override async ValueTask<IEnumerable<CheckpointInfo>> RetrieveIndexAsync(
        string sessionId,
        CheckpointInfo? withParent = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        await EnsureInitializedAsync().ConfigureAwait(false);
        await using var connection = await OpenAsync().ConfigureAwait(false);
        await using var command = CreateCommand(connection, """
            SELECT checkpoint_id, parent_checkpoint_id FROM maf_checkpoints
            WHERE session_id = $session_id
            ORDER BY rowid DESC;
            """);
        command.Parameters.AddWithValue("$session_id", sessionId);
        var results = new List<CheckpointInfo>();
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            var parentId = GetNullableString(reader, 1);
            if (withParent is not null && parentId != withParent.CheckpointId)
                continue;
            results.Add(new CheckpointInfo(sessionId, reader.GetString(0)));
        }
        return results;
    }

    private async ValueTask EnsureInitializedAsync()
    {
        if (initialized)
            return;
        await initializationLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (initialized)
                return;
            await using var connection = await OpenAsync().ConfigureAwait(false);
            if (options.EnableWal)
            {
                await using var command = CreateCommand(
                    connection,
                    "PRAGMA journal_mode = WAL;");
                await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            await using var create = CreateCommand(connection, Schema);
            await create.ExecuteNonQueryAsync().ConfigureAwait(false);
            initialized = true;
        }
        finally
        {
            initializationLock.Release();
        }
    }

    private async ValueTask<SqliteConnection> OpenAsync()
    {
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        return connection;
    }

    private static SqliteCommand CreateCommand(
        SqliteConnection connection,
        string text)
    {
        var command = connection.CreateCommand();
        command.CommandText = text;
        return command;
    }

    private static object DbValue(string? value) => value ?? (object)DBNull.Value;

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToString("O", CultureInfo.InvariantCulture);

    private static string? GetNullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
}
