using Microsoft.Data.Sqlite;
using System.Diagnostics;
using Penghou.Zhinu.Sqlite.Persistence;

namespace Penghou.Zhinu.Sqlite;

/// <summary>
/// Owns the SQLite connection string, the idempotent schema initialization,
/// and connection opening (foreign keys and busy timeout PRAGMAs). Shared by
/// every repository and by other components persisting to the same database
/// path (such as the Agent checkpoint store) so initialization state and
/// pragmas stay consistent.
/// </summary>
public sealed class SqliteDatabase : IZhinuSqliteDatabase
{
    private readonly string connectionString;
    private readonly SemaphoreSlim initializationLock = new(1, 1);
    private volatile bool initialized;

    public SqliteDatabase(ZhinuSqliteOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        Options = options;
        TimeProvider = options.TimeProvider ?? TimeProvider.System;
        var path = Path.GetFullPath(options.DatabasePath);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = options.Pooling
        }.ToString();
    }

    public ZhinuSqliteOptions Options { get; }

    public TimeProvider TimeProvider { get; }

    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (initialized)
            return;
        await initializationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (initialized)
                return;
            using var activity = Options.EnableDetailedDiagnostics
                ? ZhinuSqliteDiagnostics.ActivitySource.StartActivity(
                    ZhinuSqliteDiagnostics.InitializeActivity)
                : null;
            await using var connection = await OpenAsync(cancellationToken)
                .ConfigureAwait(false);
            await VerifySchemaCompatibilityAsync(connection, cancellationToken)
                .ConfigureAwait(false);
            if (Options.EnableWal)
            {
                await SqliteStoreSupport.ExecuteAsync(
                    connection,
                    null,
                    "PRAGMA journal_mode = WAL;",
                    cancellationToken).ConfigureAwait(false);
            }
            // Schema creation is idempotent for databases created by this
            // package version. Pre-release schema upgrades are not supported.
            await SqliteStoreSupport.ExecuteAsync(
                connection,
                null,
                Schema,
                cancellationToken).ConfigureAwait(false);
            await SqliteStoreSupport.ExecuteAsync(
                connection,
                null,
                "CREATE INDEX IF NOT EXISTS ix_workflow_runs_parent" +
                " ON workflow_runs(parent_run_id);",
                cancellationToken).ConfigureAwait(false);
            await SqliteStoreSupport.ExecuteAsync(
                connection,
                null,
                "CREATE INDEX IF NOT EXISTS ix_workflow_runs_name_version" +
                " ON workflow_runs(workflow_name, workflow_version);",
                cancellationToken).ConfigureAwait(false);
            initialized = true;
        }
        finally
        {
            initializationLock.Release();
        }
    }

    public async ValueTask EnsureInitializedAsync(CancellationToken cancellationToken = default)
    {
        if (!initialized)
            await InitializeAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask CheckpointAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await SqliteStoreSupport.ExecuteAsync(
            connection, null, "PRAGMA wal_checkpoint(TRUNCATE);", cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask VacuumAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await SqliteStoreSupport.ExecuteAsync(
            connection, null, "VACUUM;", cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        using var activity = Options.EnableDetailedDiagnostics
            ? ZhinuSqliteDiagnostics.ActivitySource.StartActivity(
                ZhinuSqliteDiagnostics.ConnectionOpenActivity,
                ActivityKind.Client)
            : null;
        var started = Stopwatch.GetTimestamp();
        var connection = new SqliteConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await SqliteStoreSupport.ExecuteAsync(
                connection, null, "PRAGMA foreign_keys = ON;", cancellationToken)
                .ConfigureAwait(false);
            await SqliteStoreSupport.ExecuteAsync(
                connection, null,
                $"PRAGMA busy_timeout = {(long)Options.BusyTimeout.TotalMilliseconds};",
                cancellationToken).ConfigureAwait(false);
            activity?.SetStatus(ActivityStatusCode.Ok);
            return connection;
        }
        catch (SqliteException exception)
        {
            ZhinuSqliteDiagnostics.ConnectionFailures.Add(1);
            if (exception.SqliteErrorCode is 5 or 6)
                ZhinuSqliteDiagnostics.ConnectionBusy.Add(1);
            activity?.SetStatus(ActivityStatusCode.Error);
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            ZhinuSqliteDiagnostics.OpenDuration.Record(
                Stopwatch.GetElapsedTime(started).TotalSeconds);
        }
    }

    private static async ValueTask VerifySchemaCompatibilityAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var countCommand = SqliteStoreSupport.CreateCommand(connection, null, """
            SELECT COUNT(*) FROM sqlite_master
            WHERE type = 'table' AND name NOT LIKE 'sqlite_%';
            """);
        var tableCount = Convert.ToInt32(
            await countCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
        if (tableCount == 0)
            return;

        await using var metadataCommand = SqliteStoreSupport.CreateCommand(connection, null, """
            SELECT COUNT(*) FROM sqlite_master
            WHERE type = 'table' AND name = 'zhinu_schema';
            """);
        var hasMetadata = Convert.ToInt32(
            await metadataCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) == 1;
        if (!hasMetadata)
        {
            throw new ZhinuSchemaCompatibilityException(
                ZhinuSqliteSchema.CurrentVersion,
                databaseVersion: null);
        }

        await using var versionCommand = SqliteStoreSupport.CreateCommand(
            connection, null, "SELECT version FROM zhinu_schema WHERE id = 1;");
        var value = await versionCommand.ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false);
        var version = value is null or DBNull ? (int?)null : Convert.ToInt32(value);
        if (version != ZhinuSqliteSchema.CurrentVersion)
        {
            throw new ZhinuSchemaCompatibilityException(
                ZhinuSqliteSchema.CurrentVersion,
                version);
        }
    }

    private static readonly string Schema = $"""
        CREATE TABLE IF NOT EXISTS zhinu_schema
        (
            id INTEGER PRIMARY KEY CHECK (id = 1),
            version INTEGER NOT NULL
        );
        INSERT OR IGNORE INTO zhinu_schema (id, version) VALUES (1, {ZhinuSqliteSchema.CurrentVersion});

        CREATE TABLE IF NOT EXISTS workflow_runs
        (
            id TEXT PRIMARY KEY,
            workflow_name TEXT NOT NULL,
            workflow_version TEXT NOT NULL,
            status INTEGER NOT NULL,
            input_json TEXT NULL,
            input_type TEXT NULL,
            output_json TEXT NULL,
            output_type TEXT NULL,
            error_json TEXT NULL,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            completed_at TEXT NULL,
            deadline TEXT NULL,
            metadata_json TEXT NULL,
            parent_run_id TEXT NULL,
            source_run_id TEXT NULL,
            trace_id TEXT NULL,
            lease_owner TEXT NULL,
            lease_expires_at TEXT NULL,
            lease_generation INTEGER NOT NULL DEFAULT 1,
            definition_fingerprint TEXT NULL,
            CHECK (status BETWEEN 0 AND 6),
            CHECK (lease_generation >= 1)
        );
        CREATE INDEX IF NOT EXISTS ix_workflow_runs_runnable
            ON workflow_runs(status, lease_expires_at, created_at);
        CREATE INDEX IF NOT EXISTS ix_workflow_runs_created
            ON workflow_runs(created_at, id);
        CREATE INDEX IF NOT EXISTS ix_workflow_runs_source
            ON workflow_runs(source_run_id);

        CREATE TABLE IF NOT EXISTS workflow_steps
        (
            id TEXT PRIMARY KEY,
            workflow_run_id TEXT NOT NULL,
            step_key TEXT NOT NULL,
            status INTEGER NOT NULL,
            attempt INTEGER NOT NULL,
            input_json TEXT NULL,
            input_type TEXT NULL,
            input_hash TEXT NULL,
            output_json TEXT NULL,
            output_type TEXT NULL,
            error_json TEXT NULL,
            signal_name TEXT NULL,
            created_at TEXT NOT NULL,
            started_at TEXT NULL,
            completed_at TEXT NULL,
            available_at TEXT NULL,
            lease_owner TEXT NULL,
            lease_expires_at TEXT NULL,
            revision INTEGER NOT NULL DEFAULT 1,
            lease_generation INTEGER NOT NULL DEFAULT 1,
            UNIQUE(workflow_run_id, step_key, revision),
            CHECK (status BETWEEN 0 AND 5),
            CHECK (attempt >= 0),
            CHECK (revision >= 1),
            CHECK (lease_generation >= 1),
            FOREIGN KEY(workflow_run_id) REFERENCES workflow_runs(id) ON DELETE CASCADE
        );
        CREATE INDEX IF NOT EXISTS ix_workflow_steps_run
            ON workflow_steps(workflow_run_id, created_at);
        CREATE INDEX IF NOT EXISTS ix_workflow_steps_runnable
            ON workflow_steps(status, available_at, lease_expires_at);
        CREATE INDEX IF NOT EXISTS ix_workflow_steps_current
            ON workflow_steps(workflow_run_id, step_key, revision);

        CREATE TABLE IF NOT EXISTS workflow_step_dependencies
        (
            run_id TEXT NOT NULL,
            step_key TEXT NOT NULL,
            depends_on_step_key TEXT NOT NULL,
            created_at TEXT NOT NULL,
            PRIMARY KEY (run_id, step_key, depends_on_step_key),
            CHECK (step_key <> depends_on_step_key),
            FOREIGN KEY(run_id) REFERENCES workflow_runs(id) ON DELETE CASCADE
        );
        CREATE INDEX IF NOT EXISTS ix_workflow_step_dependencies_key
            ON workflow_step_dependencies(run_id, step_key);
        CREATE INDEX IF NOT EXISTS ix_workflow_step_dependencies_depends_on
            ON workflow_step_dependencies(run_id, depends_on_step_key);

        CREATE TABLE IF NOT EXISTS workflow_artifacts
        (
            id TEXT PRIMARY KEY,
            workflow_run_id TEXT NOT NULL,
            name TEXT NOT NULL,
            revision INTEGER NOT NULL,
            artifact_type TEXT NOT NULL,
            artifact_version TEXT NULL,
            location TEXT NOT NULL,
            content_hash TEXT NULL,
            metadata_json TEXT NULL,
            producer_step_key TEXT NULL,
            producer_step_revision INTEGER NULL,
            created_at TEXT NOT NULL,
            UNIQUE(workflow_run_id, name, revision),
            CHECK (revision >= 1),
            FOREIGN KEY(workflow_run_id) REFERENCES workflow_runs(id) ON DELETE CASCADE
        );
        CREATE INDEX IF NOT EXISTS ix_workflow_artifacts_run
            ON workflow_artifacts(workflow_run_id, name, revision);
        CREATE INDEX IF NOT EXISTS ix_workflow_artifacts_created
            ON workflow_artifacts(workflow_run_id, created_at, name, revision);
        CREATE INDEX IF NOT EXISTS ix_workflow_artifacts_producer
            ON workflow_artifacts(workflow_run_id, producer_step_key,
                producer_step_revision);

        CREATE TABLE IF NOT EXISTS workflow_events
        (
            sequence INTEGER PRIMARY KEY AUTOINCREMENT,
            workflow_run_id TEXT NOT NULL,
            step_key TEXT NULL,
            event_type TEXT NOT NULL,
            timestamp TEXT NOT NULL,
            attempt INTEGER NULL,
            data_json TEXT NULL,
            FOREIGN KEY(workflow_run_id) REFERENCES workflow_runs(id) ON DELETE CASCADE
        );
        CREATE INDEX IF NOT EXISTS ix_workflow_events_run_sequence
            ON workflow_events(workflow_run_id, sequence);

        CREATE TABLE IF NOT EXISTS workflow_signals
        (
            id TEXT PRIMARY KEY,
            workflow_run_id TEXT NOT NULL,
            signal_name TEXT NOT NULL,
            data_json TEXT NULL,
            delivered_step_id TEXT NULL,
            created_at TEXT NOT NULL,
            FOREIGN KEY(workflow_run_id) REFERENCES workflow_runs(id) ON DELETE CASCADE
        );
        CREATE INDEX IF NOT EXISTS ix_workflow_signals_run_name
            ON workflow_signals(workflow_run_id, signal_name, delivered_step_id, created_at);

        CREATE TABLE IF NOT EXISTS workflow_step_compensations
        (
            id TEXT PRIMARY KEY,
            workflow_run_id TEXT NOT NULL,
            step_key TEXT NOT NULL,
            revision INTEGER NOT NULL,
            compensation_name TEXT NOT NULL,
            status INTEGER NOT NULL,
            attempt INTEGER NOT NULL,
            input_json TEXT NULL,
            input_type TEXT NULL,
            output_json TEXT NULL,
            error_json TEXT NULL,
            retry_policy_json TEXT NULL,
            timeout_ticks INTEGER NULL,
            available_at TEXT NULL,
            timeout_at TEXT NULL,
            lease_owner TEXT NULL,
            lease_expires_at TEXT NULL,
            lease_generation INTEGER NOT NULL DEFAULT 1,
            started_at TEXT NULL,
            completed_at TEXT NULL,
            created_at TEXT NOT NULL,
            actor TEXT NULL,
            reason TEXT NULL,
            idempotency_key TEXT NULL,
            UNIQUE(workflow_run_id, step_key, revision),
            CHECK (status BETWEEN 0 AND 4),
            CHECK (attempt >= 0),
            FOREIGN KEY(workflow_run_id) REFERENCES workflow_runs(id) ON DELETE CASCADE
        );
        CREATE INDEX IF NOT EXISTS ix_workflow_step_compensations_run
            ON workflow_step_compensations(workflow_run_id, step_key);

        CREATE TABLE IF NOT EXISTS workflow_run_operations
        (
            operation_id TEXT PRIMARY KEY,
            workflow_run_id TEXT NOT NULL,
            operation_type TEXT NOT NULL,
            status INTEGER NOT NULL,
            payload_json TEXT NULL,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            completed_at TEXT NULL,
            CHECK (status BETWEEN 0 AND 5),
            FOREIGN KEY(workflow_run_id) REFERENCES workflow_runs(id) ON DELETE CASCADE
        );
        CREATE INDEX IF NOT EXISTS ix_workflow_run_operations_run
            ON workflow_run_operations(workflow_run_id, status, created_at);
        """;
}
