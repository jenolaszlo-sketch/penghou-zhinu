using Microsoft.Data.Sqlite;

namespace Penghou.Zhinu.Sqlite.Persistence;

/// <summary>
/// Owns the SQLite connection string, the idempotent schema initialization,
/// and connection opening (foreign keys and busy timeout PRAGMAs). Shared by
/// every repository so initialization state and pragmas stay consistent.
/// </summary>
internal sealed class SqliteConnectionFactory
{
    private readonly ZhinuSqliteOptions options;
    private readonly string connectionString;
    private readonly SemaphoreSlim initializationLock = new(1, 1);
    private volatile bool initialized;

    public SqliteConnectionFactory(ZhinuSqliteOptions options)
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

    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (initialized)
            return;
        await initializationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (initialized)
                return;
            await using var connection = await OpenAsync(cancellationToken)
                .ConfigureAwait(false);
            if (options.EnableWal)
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

    public async ValueTask<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await SqliteStoreSupport.ExecuteAsync(
            connection,
            null,
            "PRAGMA foreign_keys = ON;",
            cancellationToken).ConfigureAwait(false);
        await SqliteStoreSupport.ExecuteAsync(
            connection,
            null,
            $"PRAGMA busy_timeout = {(long)options.BusyTimeout.TotalMilliseconds};",
            cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private const string Schema = """
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
            lease_owner TEXT NULL,
            lease_expires_at TEXT NULL,
            lease_generation INTEGER NOT NULL DEFAULT 1
        );
        CREATE INDEX IF NOT EXISTS ix_workflow_runs_runnable
            ON workflow_runs(status, lease_expires_at, created_at);
        CREATE INDEX IF NOT EXISTS ix_workflow_runs_created
            ON workflow_runs(created_at, id);

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
            FOREIGN KEY(run_id) REFERENCES workflow_runs(id) ON DELETE CASCADE
        );
        CREATE INDEX IF NOT EXISTS ix_workflow_step_dependencies_key
            ON workflow_step_dependencies(run_id, step_key);
        CREATE INDEX IF NOT EXISTS ix_workflow_step_dependencies_depends_on
            ON workflow_step_dependencies(run_id, depends_on_step_key);

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
            ON workflow_signals(workflow_run_id, signal_name, delivered_step_id);

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
            FOREIGN KEY(workflow_run_id) REFERENCES workflow_runs(id) ON DELETE CASCADE
        );
        CREATE INDEX IF NOT EXISTS ix_workflow_run_operations_run
            ON workflow_run_operations(workflow_run_id, status, created_at);
        """;
}
