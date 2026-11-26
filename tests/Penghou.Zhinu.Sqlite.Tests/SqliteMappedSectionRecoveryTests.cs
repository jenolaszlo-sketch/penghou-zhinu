using FluentAssertions;
using Microsoft.Data.Sqlite;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite.Recovery;

#pragma warning disable xUnit1051 // Controlled cancellation tokens are intentional in this suite.

namespace Penghou.Zhinu.Sqlite.Tests;

/// <summary>
/// Deterministic qualification of the Windows mapped-section recovery gate.
/// The production classifier and retry loop are exercised through the real
/// store; only the native reader, clock, delay and attempt injection are
/// replaced. This is error injection, not a reproduction of the physical race.
/// </summary>
public sealed class SqliteMappedSectionRecoveryTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "zhinu-mapped-recovery",
        Guid.NewGuid().ToString("N"));

    public SqliteMappedSectionRecoveryTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static SqliteException MappedFailure() =>
        new("SQLite Error 10: 'disk I/O error'.", 10, 1546);

    private static SqliteNativeErrorState MappedState => new(10, 1546, 1224);

    private sealed class FakeReader : ISqliteNativeErrorStateReader
    {
        public bool Available { get; set; } = true;

        public SqliteNativeErrorState State { get; set; } = MappedState;

        public bool TryRead(SqliteConnection connection, out SqliteNativeErrorState state)
        {
            state = State;
            return Available;
        }
    }

    private sealed class RuntimeScope : IDisposable
    {
        public RuntimeScope(SqliteRecoveryRuntime runtime) =>
            SqliteRecoveryTestHooks.Runtime.Value = runtime;

        public void Dispose() => SqliteRecoveryTestHooks.Runtime.Value = null;
    }

    private sealed class TestDatabase : IZhinuSqliteDatabase, IZhinuSqliteDatabaseRecovery
    {
        private readonly string path;

        public TestDatabase(string path)
        {
            this.path = path;
            Options = new ZhinuSqliteOptions { DatabasePath = path, Pooling = false };
        }

        public int Opens { get; private set; }

        public List<bool> Fresh { get; } = [];

        public bool ThrowOnFresh { get; set; }

        public ZhinuSqliteOptions Options { get; }

        public TimeProvider TimeProvider => TimeProvider.System;

        public ValueTask InitializeAsync(CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask EnsureInitializedAsync(CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask<SqliteConnection> OpenAsync(CancellationToken cancellationToken = default) =>
            Open(fresh: false, cancellationToken);

        public ValueTask<SqliteConnection> OpenFreshAsync(CancellationToken cancellationToken) =>
            Open(fresh: true, cancellationToken);

        private async ValueTask<SqliteConnection> Open(
            bool fresh,
            CancellationToken cancellationToken)
        {
            Opens++;
            Fresh.Add(fresh);
            if (fresh && ThrowOnFresh)
                throw new IOException("reopen failed");

            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Pooling = false
            }.ToString());
            await connection.OpenAsync(cancellationToken);
            return connection;
        }

        public ValueTask CheckpointAsync(CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask VacuumAsync(CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }

    private sealed class ThrowingDisposeDatabase : IZhinuSqliteDatabase, IZhinuSqliteDatabaseRecovery
    {
        private readonly string path;

        public ThrowingDisposeDatabase(string path)
        {
            this.path = path;
            Options = new ZhinuSqliteOptions { DatabasePath = path, Pooling = false };
        }

        public int Opens { get; private set; }

        public ZhinuSqliteOptions Options { get; }

        public TimeProvider TimeProvider => TimeProvider.System;

        public ValueTask InitializeAsync(CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask EnsureInitializedAsync(CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask<SqliteConnection> OpenAsync(CancellationToken cancellationToken = default) =>
            Open(cancellationToken);

        public ValueTask<SqliteConnection> OpenFreshAsync(CancellationToken cancellationToken) =>
            Open(cancellationToken);

        private async ValueTask<SqliteConnection> Open(CancellationToken cancellationToken)
        {
            Opens++;
            var connection = new ThrowingDisposeConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Pooling = false
            }.ToString());
            await connection.OpenAsync(cancellationToken);
            return connection;
        }

        public ValueTask CheckpointAsync(CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask VacuumAsync(CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }

    private sealed class ThrowingDisposeConnection : SqliteConnection
    {
        public ThrowingDisposeConnection(string connectionString) : base(connectionString)
        {
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                throw new InvalidOperationException("dispose failed");
            base.Dispose(disposing);
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long timestamp;

        public ManualTimeProvider(DateTimeOffset now) => timestamp = now.UtcTicks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => timestamp;

        public override DateTimeOffset GetUtcNow() => new(timestamp, TimeSpan.Zero);

        public void Advance(TimeSpan delta) => timestamp += delta.Ticks;
    }

    private sealed class FixedElapsedTimeProvider : TimeProvider
    {
        private readonly TimeSpan elapsed;

        public FixedElapsedTimeProvider(TimeSpan elapsed) => this.elapsed = elapsed;

        private int calls;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() =>
            calls++ == 0 ? 0 : (long)(elapsed.TotalSeconds * TimestampFrequency);
    }

    private sealed class BlockingWorkflow : IWorkflow<string, string>
    {
        private readonly TaskCompletionSource started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => started.Task;

        public async Task<string> RunAsync(
            WorkflowContext context,
            string input,
            CancellationToken cancellationToken)
        {
            return await context.StepAsync(
                "work",
                input,
                async (value, token) =>
                {
                    started.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    return value;
                },
                cancellationToken: cancellationToken);
        }
    }

    [Fact]
    public async Task Store_schema_verify_retries_once_and_initializes()
    {
        var path = Path.Combine(root, "schema.db");
        var store = new SqliteWorkflowStore(new ZhinuSqliteOptions
        {
            DatabasePath = path,
            Pooling = true,
            BusyTimeout = TimeSpan.FromSeconds(5)
        });
        var attempts = new List<int>();
        var delays = new List<TimeSpan>();
        var opened = new List<(bool Pooling, int ForeignKeys, int BusyTimeout)>();
        var connections = new List<SqliteConnection>();
        var runtime = new SqliteRecoveryRuntime(
            new FakeReader(),
            (delay, _) =>
            {
                delays.Add(delay);
                return ValueTask.CompletedTask;
            },
            TimeProvider.System,
            isWindows: true,
            (operation, attempt) =>
            {
                if (operation != SqliteRecoveryOperation.SchemaVerify)
                    return null;
                attempts.Add(attempt);
                return attempt == 1 ? MappedFailure() : null;
            },
            (connection, _) =>
            {
                connections.Add(connection);
                using var pragma = connection.CreateCommand();
                pragma.CommandText = "PRAGMA foreign_keys;";
                var foreignKeys = Convert.ToInt32(pragma.ExecuteScalar());
                pragma.CommandText = "PRAGMA busy_timeout;";
                var busyTimeout = Convert.ToInt32(pragma.ExecuteScalar());
                opened.Add((
                    new SqliteConnectionStringBuilder(connection.ConnectionString).Pooling,
                    foreignKeys,
                    busyTimeout));
            });

        using (new RuntimeScope(runtime))
            await store.InitializeAsync();

        attempts.Should().Equal(1, 2);
        delays.Should().Equal(TimeSpan.FromMilliseconds(25));
        opened.Select(item => item.Pooling).Should().Equal(true, false);
        opened.Should().OnlyContain(item =>
            item.ForeignKeys == 1 && item.BusyTimeout == 5000);
        connections.Should().HaveCount(2);
        ReferenceEquals(connections[0], connections[1]).Should().BeFalse();

        await using var verify = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false
        }.ToString());
        await verify.OpenAsync();
        using var command = verify.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master;";
        Convert.ToInt32(await command.ExecuteScalarAsync())
            .Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Store_lease_recovery_retries_the_begin_and_commits()
    {
        var path = Path.Combine(root, "lease.db");
        var store = new SqliteWorkflowStore(new ZhinuSqliteOptions
        {
            DatabasePath = path,
            Pooling = true,
            BusyTimeout = TimeSpan.FromSeconds(5)
        });
        await store.InitializeAsync();

        var attempts = new List<int>();
        var delays = new List<TimeSpan>();
        var runtime = new SqliteRecoveryRuntime(
            new FakeReader(),
            (delay, _) =>
            {
                delays.Add(delay);
                return ValueTask.CompletedTask;
            },
            TimeProvider.System,
            isWindows: true,
            (operation, attempt) =>
            {
                if (operation != SqliteRecoveryOperation.LeaseRecoverBegin)
                    return null;
                attempts.Add(attempt);
                return attempt == 1 ? MappedFailure() : null;
            });

        int recovered;
        using (new RuntimeScope(runtime))
            recovered = await store.RecoverExpiredLeasesAsync(DateTimeOffset.UtcNow);

        recovered.Should().Be(0);
        attempts.Should().Equal(1, 2);
        delays.Should().Equal(TimeSpan.FromMilliseconds(25));
    }

    [Fact]
    public async Task All_permitted_retry_positions_succeed()
    {
        for (var failures = 0; failures <= 5; failures++)
        {
            var database = new TestDatabase(Path.Combine(root, $"positions-{failures}.db"));
            var delays = new List<TimeSpan>();
            var runtime = new SqliteRecoveryRuntime(
                new FakeReader(),
                (delay, _) =>
                {
                    delays.Add(delay);
                    return ValueTask.CompletedTask;
                },
                TimeProvider.System,
                isWindows: true,
                (_, attempt) => attempt <= failures ? MappedFailure() : null);

            using (new RuntimeScope(runtime))
            {
                var (connection, version) = await SqliteTransientRecovery.ExecuteSchemaVerifyAsync(
                    database,
                    (_, _) => ValueTask.FromResult(7),
                    CancellationToken.None);
                await connection.DisposeAsync();
                version.Should().Be(7);
            }

            database.Opens.Should().Be(failures + 1);
            delays.Should().HaveCount(failures);
            if (failures > 0)
                delays.Should().Equal(new[]
                {
                    TimeSpan.FromMilliseconds(25),
                    TimeSpan.FromMilliseconds(50),
                    TimeSpan.FromMilliseconds(100),
                    TimeSpan.FromMilliseconds(200),
                    TimeSpan.FromMilliseconds(400)
                }.Take(failures));
        }
    }

    [Fact]
    public async Task Exhaustion_stops_at_six_attempts_and_preserves_native_codes()
    {
        var database = new TestDatabase(Path.Combine(root, "exhaustion.db"));
        var delays = new List<TimeSpan>();
        var runtime = new SqliteRecoveryRuntime(
            new FakeReader(),
            (delay, _) =>
            {
                delays.Add(delay);
                return ValueTask.CompletedTask;
            },
            TimeProvider.System,
            isWindows: true,
            (_, _) => MappedFailure());

        using (new RuntimeScope(runtime))
        {
            Func<Task> act = () => SqliteTransientRecovery.ExecuteSchemaVerifyAsync(
                database,
                (_, _) => ValueTask.FromResult(0),
                CancellationToken.None).AsTask();

            var exception = (await act.Should().ThrowAsync<SqliteException>()).Which;
            exception.SqliteErrorCode.Should().Be(10);
            exception.SqliteExtendedErrorCode.Should().Be(1546);
            exception.Data[SqliteTransientRecovery.AttemptsKey].Should().Be(6);
            exception.Data[SqliteTransientRecovery.ReasonKey].Should().Be("attempt-limit");
            exception.Data[SqliteTransientRecovery.PrimaryKey].Should().Be(10);
            exception.Data[SqliteTransientRecovery.ExtendedKey].Should().Be(1546);
            exception.Data[SqliteTransientRecovery.SystemErrnoKey].Should().Be(1224);
        }

        database.Opens.Should().Be(6);
        delays.Should().HaveCount(5);
    }

    [Fact]
    public async Task Budget_exhaustion_stops_before_the_next_retry()
    {
        var database = new TestDatabase(Path.Combine(root, "budget.db"));
        var delays = new List<TimeSpan>();
        var runtime = new SqliteRecoveryRuntime(
            new FakeReader(),
            (delay, _) =>
            {
                delays.Add(delay);
                return ValueTask.CompletedTask;
            },
            new FixedElapsedTimeProvider(TimeSpan.FromSeconds(2.5)),
            isWindows: true,
            (_, _) => MappedFailure());

        using (new RuntimeScope(runtime))
        {
            Func<Task> act = () => SqliteTransientRecovery.ExecuteSchemaVerifyAsync(
                database,
                (_, _) => ValueTask.FromResult(0),
                CancellationToken.None).AsTask();

            var exception = (await act.Should().ThrowAsync<SqliteException>()).Which;
            exception.Data[SqliteTransientRecovery.ReasonKey].Should().Be("budget");
            exception.Data[SqliteTransientRecovery.AttemptsKey].Should().Be(1);
        }

        database.Opens.Should().Be(1);
        delays.Should().BeEmpty();
    }

    [Theory]
    [InlineData(10, 1546, 10, 1546, 1224, true, true, true)]
    [InlineData(5, 5, 10, 1546, 1224, true, true, false)]
    [InlineData(6, 6, 10, 1546, 1224, true, true, false)]
    [InlineData(11, 11, 10, 1546, 1224, true, true, false)]
    [InlineData(10, 3338, 10, 1546, 1224, true, true, false)]
    [InlineData(10, 2570, 10, 1546, 1224, true, true, false)]
    [InlineData(10, 1546, 10, 2570, 1224, true, true, false)]
    [InlineData(10, 1546, 10, 1546, 32, true, true, false)]
    [InlineData(10, 1546, 10, 1546, 1234, true, true, false)]
    [InlineData(10, 1546, 10, 1546, 1224, false, true, false)]
    [InlineData(10, 1546, 10, 1546, 1224, true, false, false)]
    [InlineData(10, 1546, 5, 1546, 1224, true, true, false)]
    public async Task Classification_requires_every_condition(
        int exceptionPrimary,
        int exceptionExtended,
        int statePrimary,
        int stateExtended,
        int errno,
        bool available,
        bool windows,
        bool retried)
    {
        var database = new TestDatabase(Path.Combine(root, $"classification-{Guid.NewGuid():N}.db"));
        var delays = new List<TimeSpan>();
        var reader = new FakeReader
        {
            Available = available,
            State = new SqliteNativeErrorState(statePrimary, stateExtended, errno)
        };
        var runtime = new SqliteRecoveryRuntime(
            reader,
            (delay, _) =>
            {
                delays.Add(delay);
                return ValueTask.CompletedTask;
            },
            TimeProvider.System,
            windows,
            (_, attempt) => attempt == 1
                ? new SqliteException("injected", exceptionPrimary, exceptionExtended)
                : null);

        using (new RuntimeScope(runtime))
        {
            if (retried)
            {
                var (connection, _) = await SqliteTransientRecovery.ExecuteSchemaVerifyAsync(
                    database,
                    (_, _) => ValueTask.FromResult(0),
                    CancellationToken.None);
                await connection.DisposeAsync();
                database.Opens.Should().Be(2);
                delays.Should().Equal(TimeSpan.FromMilliseconds(25));
            }
            else
            {
                Func<Task> act = () => SqliteTransientRecovery.ExecuteSchemaVerifyAsync(
                    database,
                    (_, _) => ValueTask.FromResult(0),
                    CancellationToken.None).AsTask();
                await act.Should().ThrowAsync<SqliteException>();
                database.Opens.Should().Be(1);
                delays.Should().BeEmpty();
            }
        }
    }

    [Fact]
    public async Task Canceled_token_does_not_open_or_retry()
    {
        var database = new TestDatabase(Path.Combine(root, "cancel-initial.db"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Func<Task> act = () => SqliteTransientRecovery.ExecuteSchemaVerifyAsync(
            database,
            (_, _) => ValueTask.FromResult(0),
            cancellation.Token).AsTask();

        await act.Should().ThrowAsync<OperationCanceledException>();
        database.Opens.Should().Be(0);
    }

    [Fact]
    public async Task Cancellation_during_backoff_stops_and_propagates()
    {
        var database = new TestDatabase(Path.Combine(root, "cancel-backoff.db"));
        using var cancellation = new CancellationTokenSource();
        var delays = new List<TimeSpan>();
        var runtime = new SqliteRecoveryRuntime(
            new FakeReader(),
            (delay, token) =>
            {
                delays.Add(delay);
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
                return ValueTask.CompletedTask;
            },
            TimeProvider.System,
            isWindows: true,
            (_, attempt) => attempt == 1 ? MappedFailure() : null);

        using (new RuntimeScope(runtime))
        {
            Func<Task> act = () => SqliteTransientRecovery.ExecuteSchemaVerifyAsync(
                database,
                (_, _) => ValueTask.FromResult(0),
                cancellation.Token).AsTask();
            await act.Should().ThrowAsync<OperationCanceledException>();
        }

        database.Opens.Should().Be(1);
        delays.Should().Equal(TimeSpan.FromMilliseconds(25));
    }

    [Fact]
    public async Task Disposal_failure_preserves_the_original_and_stops()
    {
        var database = new ThrowingDisposeDatabase(Path.Combine(root, "dispose.db"));
        var runtime = new SqliteRecoveryRuntime(
            new FakeReader(),
            (_, _) => ValueTask.CompletedTask,
            TimeProvider.System,
            isWindows: true,
            (_, attempt) => attempt == 1 ? MappedFailure() : null);

        using (new RuntimeScope(runtime))
        {
            Func<Task> act = () => SqliteTransientRecovery.ExecuteSchemaVerifyAsync(
                database,
                (_, _) => ValueTask.FromResult(0),
                CancellationToken.None).AsTask();

            var exception = (await act.Should().ThrowAsync<SqliteException>()).Which;
            exception.SqliteErrorCode.Should().Be(10);
            exception.Data[SqliteTransientRecovery.ReasonKey].Should().Be("dispose-failure");
            exception.Data[SqliteTransientRecovery.DisposeFailureKey]
                .Should().BeOfType<InvalidOperationException>();
        }

        database.Opens.Should().Be(1);
    }

    [Fact]
    public async Task Reopen_failure_preserves_the_original_and_stops()
    {
        var database = new TestDatabase(Path.Combine(root, "reopen.db"))
        {
            ThrowOnFresh = true
        };
        var delays = new List<TimeSpan>();
        var runtime = new SqliteRecoveryRuntime(
            new FakeReader(),
            (delay, _) =>
            {
                delays.Add(delay);
                return ValueTask.CompletedTask;
            },
            TimeProvider.System,
            isWindows: true,
            (_, attempt) => attempt == 1 ? MappedFailure() : null);

        using (new RuntimeScope(runtime))
        {
            Func<Task> act = () => SqliteTransientRecovery.ExecuteSchemaVerifyAsync(
                database,
                (_, _) => ValueTask.FromResult(0),
                CancellationToken.None).AsTask();

            var exception = (await act.Should().ThrowAsync<SqliteException>()).Which;
            exception.SqliteErrorCode.Should().Be(10);
            exception.Data[SqliteTransientRecovery.ReopenFailureKey]
                .Should().BeOfType<IOException>();
        }

        database.Opens.Should().Be(2);
        delays.Should().Equal(TimeSpan.FromMilliseconds(25));
    }

    [Fact]
    public async Task Matching_failure_after_begin_is_not_replayed()
    {
        var path = Path.Combine(root, "after-begin.db");
        var store = new SqliteWorkflowStore(new ZhinuSqliteOptions
        {
            DatabasePath = path,
            Pooling = true,
            BusyTimeout = TimeSpan.FromSeconds(5)
        });
        await store.InitializeAsync();

        var beginAttempts = new List<int>();
        var delays = new List<TimeSpan>();
        var runtime = new SqliteRecoveryRuntime(
            new FakeReader(),
            (delay, _) =>
            {
                delays.Add(delay);
                return ValueTask.CompletedTask;
            },
            TimeProvider.System,
            isWindows: true,
            (operation, attempt) =>
            {
                if (operation == SqliteRecoveryOperation.LeaseRecoverBegin)
                {
                    beginAttempts.Add(attempt);
                    return null;
                }
                return null;
            });

        using var scope = new RuntimeScope(runtime);
        SqliteLeaseRepositoryTestHooks.AfterBegin.Value =
            () => new SqliteException("failure after begin", 10, 1546);
        try
        {
            Func<Task> act = () => store.RecoverExpiredLeasesAsync(DateTimeOffset.UtcNow).AsTask();
            await act.Should().ThrowAsync<WorkflowPersistenceException>();
        }
        finally
        {
            SqliteLeaseRepositoryTestHooks.AfterBegin.Value = null;
        }

        beginAttempts.Should().Equal(1);
        delays.Should().BeEmpty();
    }

    [Fact]
    public void Native_reader_reads_the_loaded_sqlite_handle()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1;";
        _ = command.ExecuteScalar();

        var reader = new SqliteNativeErrorStateReader();
        var available = reader.TryRead(connection, out var state);

        available.Should().Be(OperatingSystem.IsWindows());
        if (OperatingSystem.IsWindows())
        {
            state.PrimaryCode.Should().Be(0);
            state.ExtendedCode.Should().Be(0);
            state.SystemErrno.Should().Be(0);
        }
    }

    [Fact]
    public async Task Lease_recovery_retries_without_losing_ownership_or_duplicating_events()
    {
        var path = Path.Combine(root, "lease-fencing.db");
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var store = new SqliteWorkflowStore(new ZhinuSqliteOptions
        {
            DatabasePath = path,
            Pooling = true,
            BusyTimeout = TimeSpan.FromSeconds(5),
            TimeProvider = clock
        });
        var workflow = new BlockingWorkflow();
        await using var engine = new WorkflowEngine(
            store,
            new WorkflowRegistry().Register("blocking", "1", workflow),
            new ZhinuOptions
            {
                LeaseDuration = TimeSpan.FromSeconds(10),
                LeaseRenewalInterval = TimeSpan.FromSeconds(5),
                LeaseRecoveryInterval = TimeSpan.FromHours(1),
                PollInterval = TimeSpan.FromMilliseconds(10)
            },
            timeProvider: clock);

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var runId = await engine.StartAsync(
            "blocking", "1", "input", cancellationToken: cancellation.Token);
        var execution = engine.ExecuteAsync(runId, cancellation.Token);
        await workflow.Started.WaitAsync(TimeSpan.FromSeconds(5), cancellation.Token);
        clock.Advance(TimeSpan.FromSeconds(30));

        var beginAttempts = new List<int>();
        var delays = new List<TimeSpan>();
        var runtime = new SqliteRecoveryRuntime(
            new FakeReader(),
            (delay, _) =>
            {
                delays.Add(delay);
                return ValueTask.CompletedTask;
            },
            clock,
            isWindows: true,
            (operation, attempt) =>
            {
                if (operation != SqliteRecoveryOperation.LeaseRecoverBegin)
                    return null;
                beginAttempts.Add(attempt);
                return attempt == 1 ? MappedFailure() : null;
            });

        int recovered;
        using (new RuntimeScope(runtime))
            recovered = await store.RecoverExpiredLeasesAsync(
                clock.GetUtcNow(), cancellation.Token);

        recovered.Should().BeGreaterThan(0);
        beginAttempts.Should().Equal(1, 2);
        delays.Should().Equal(TimeSpan.FromMilliseconds(25));

        var events = await engine.GetEventsAsync(
            runId, cancellationToken: cancellation.Token);
        events.Count(item => item.EventType == WorkflowEventTypes.LeaseRecovered)
            .Should().Be(1);

        cancellation.Cancel();
        try
        {
            await execution;
        }
        catch (OperationCanceledException)
        {
        }
        catch (WorkflowLeaseException)
        {
        }
    }
}

#pragma warning restore xUnit1051
