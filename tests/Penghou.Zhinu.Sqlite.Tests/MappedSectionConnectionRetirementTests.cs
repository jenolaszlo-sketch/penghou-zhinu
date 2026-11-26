using FluentAssertions;
using Microsoft.Data.Sqlite;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite.Recovery;

#pragma warning disable xUnit1051 // Controlled cancellation tokens are intentional in this suite.

namespace Penghou.Zhinu.Sqlite.Tests;

/// <summary>
/// Q-01 connection-retirement regression controls.
///
/// Physical identity is proven with a connection-scoped TEMP table rather than
/// native pointer equality: a TEMP table survives a provider pool return but not
/// a real physical close. Each recovery attempt marks its own physical
/// connection, and every later ordinary open is checked for the earlier markers.
/// </summary>
public sealed class MappedSectionConnectionRetirementTests : IDisposable
{
    private static readonly string evidence = Path.Combine(
        Path.GetTempPath(),
        "zhinu-r1-retirement-evidence");

    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "zhinu-r1-retirement",
        Guid.NewGuid().ToString("N"));

    public MappedSectionConnectionRetirementTests()
    {
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(evidence);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        TryDelete(root);
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
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

    private sealed class FakeReader : ISqliteNativeErrorStateReader
    {
        public bool Available { get; set; } = true;

        public SqliteNativeErrorState State { get; set; } = new(10, 1546, 1224);

        public bool TryRead(SqliteConnection connection, out SqliteNativeErrorState state)
        {
            state = State;
            return Available;
        }
    }

    private sealed class Scope : IDisposable
    {
        public Scope(SqliteRecoveryRuntime runtime) =>
            SqliteRecoveryTestHooks.Runtime.Value = runtime;

        public void Dispose() => SqliteRecoveryTestHooks.Runtime.Value = null;
    }

    private sealed record Observation(
        long Handle,
        bool Pooling,
        int ForeignKeys,
        int BusyTimeout,
        bool SawEarlierMarker);

    private static string PooledString(string path) =>
        new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        }.ToString();

    private static bool HasMarker(SqliteConnection connection, int attempt)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM sqlite_temp_master " +
            $"WHERE type = 'table' AND name = 'q01_probe_{attempt}';";
        return Convert.ToInt32(command.ExecuteScalar()) > 0;
    }

    private static bool HasEarlierMarker(SqliteConnection connection, int attempt)
    {
        for (var earlier = 1; earlier < attempt; earlier++)
        {
            if (HasMarker(connection, earlier))
                return true;
        }

        return false;
    }

    private static void CreateMarker(SqliteConnection connection, int attempt)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            $"CREATE TEMP TABLE q01_probe_{attempt}(value TEXT);" +
            $"INSERT INTO q01_probe_{attempt} VALUES('attempt-{attempt}');";
        command.ExecuteNonQuery();
    }

    private static Observation Observe(SqliteConnection connection, int attempt)
    {
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys;";
        var foreignKeys = Convert.ToInt32(pragma.ExecuteScalar());
        pragma.CommandText = "PRAGMA busy_timeout;";
        var busyTimeout = Convert.ToInt32(pragma.ExecuteScalar());

        var observation = new Observation(
            connection.Handle?.DangerousGetHandle().ToInt64() ?? 0,
            new SqliteConnectionStringBuilder(connection.ConnectionString).Pooling,
            foreignKeys,
            busyTimeout,
            HasEarlierMarker(connection, attempt));

        CreateMarker(connection, attempt);
        return observation;
    }

    private static void WriteEvidence(string name, object value) =>
        File.WriteAllText(
            Path.Combine(evidence, name),
            System.Text.Json.JsonSerializer.Serialize(
                value,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

    private static bool NativeAddressWasReused(IReadOnlyList<Observation> observations) =>
        observations.Select(item => item.Handle).Distinct().Count() != observations.Count;

    private static SqliteWorkflowStore Store(string path) =>
        new(new ZhinuSqliteOptions
        {
            DatabasePath = path,
            Pooling = true,
            BusyTimeout = TimeSpan.FromSeconds(5)
        });

    [Fact]
    public void Marker_method_distinguishes_pooled_reuse_from_physical_close()
    {
        var path = Path.Combine(root, "marker.db");

        using (var first = new SqliteConnection(PooledString(path)))
        {
            first.Open();
            CreateMarker(first, 1);
        }

        using (var second = new SqliteConnection(PooledString(path)))
        {
            second.Open();
            HasMarker(second, 1).Should().BeTrue(
                "a pooled physical connection retains its temp schema across a pool return");
        }

        SqliteConnection.ClearAllPools();

        using (var third = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false
        }.ToString()))
        {
            third.Open();
            CreateMarker(third, 2);
        }

        using (var fourth = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false
        }.ToString()))
        {
            fourth.Open();
            HasMarker(fourth, 2).Should().BeFalse(
                "a non-pooled connection is closed physically and a new one has no temp schema");
        }

        WriteEvidence("marker-method.json", new
        {
            pooledMarkerSurvived = true,
            nonPooledMarkerSurvived = false
        });
    }

    [Fact]
    public async Task Schema_verify_recovery_owns_every_attempt_and_retires_the_failed_connection()
    {
        var path = Path.Combine(root, "schema.db");
        var observations = new List<Observation>();
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
                operation == SqliteRecoveryOperation.SchemaVerify && attempt == 1
                    ? MappedFailure()
                    : null,
            (connection, attempt) => observations.Add(Observe(connection, attempt)));

        var store = Store(path);
        using (new Scope(runtime))
            await store.InitializeAsync();

        observations.Should().HaveCount(2);
        observations.Should().OnlyContain(item => !item.Pooling);
        observations.Should().OnlyContain(item => item.ForeignKeys == 1 && item.BusyTimeout == 5000);
        observations.Select(item => item.Handle).Should().NotContain(0);
        observations[1].SawEarlierMarker.Should().BeFalse(
            "the retry must be a new physical connection, not the failed one");
        delays.Should().Equal(TimeSpan.FromMilliseconds(25));

        for (var ordinary = 0; ordinary < 10; ordinary++)
        {
            using var later = new SqliteConnection(PooledString(path));
            later.Open();
            HasMarker(later, 1).Should().BeFalse(
                "the failed attempt-1 physical connection must not be reacquired");
            HasMarker(later, 2).Should().BeFalse(
                "the retry physical connection was also recovery-owned and closed");

            using var command = later.CreateCommand();
            command.CommandText = "SELECT version FROM zhinu_schema WHERE id = 1;";
            Convert.ToInt32(command.ExecuteScalar()).Should().Be(10);
        }

        // The ordinary connection pool is still functional: a fresh pooled
        // marker survives a pool return.
        using (var pooledFirst = new SqliteConnection(PooledString(path)))
        {
            pooledFirst.Open();
            CreateMarker(pooledFirst, 99);
        }

        using (var pooledSecond = new SqliteConnection(PooledString(path)))
        {
            pooledSecond.Open();
            HasMarker(pooledSecond, 99).Should().BeTrue(
                "normal opens must keep using the configured connection pool");
        }

        WriteEvidence("schema-retirement.json", new
        {
            operation = "schema.verify",
            attempts = observations.Count,
            pooling = observations.Select(item => item.Pooling).ToArray(),
            foreignKeys = observations.Select(item => item.ForeignKeys).ToArray(),
            busyTimeout = observations.Select(item => item.BusyTimeout).ToArray(),
            sawEarlierMarker = observations.Select(item => item.SawEarlierMarker).ToArray(),
            nativeAddressReuseObserved = NativeAddressWasReused(observations),
            ordinaryOpensWithoutFailedMarker = 10,
            normalPoolStillActive = true
        });
    }

    [Fact]
    public async Task Lease_recovery_recovery_owns_every_attempt_and_retires_the_failed_connection()
    {
        var path = Path.Combine(root, "lease.db");
        var store = Store(path);
        await store.InitializeAsync();

        var observations = new List<Observation>();
        var runtime = new SqliteRecoveryRuntime(
            new FakeReader(),
            (_, _) => ValueTask.CompletedTask,
            TimeProvider.System,
            isWindows: true,
            (operation, attempt) =>
                operation == SqliteRecoveryOperation.LeaseRecoverBegin && attempt == 1
                    ? MappedFailure()
                    : null,
            (connection, attempt) => observations.Add(Observe(connection, attempt)));

        int recovered;
        using (new Scope(runtime))
            recovered = await store.RecoverExpiredLeasesAsync(DateTimeOffset.UtcNow);

        recovered.Should().Be(0);
        observations.Should().HaveCount(2);
        observations.Should().OnlyContain(item => !item.Pooling);
        observations.Should().OnlyContain(item => item.ForeignKeys == 1 && item.BusyTimeout == 5000);
        observations[1].SawEarlierMarker.Should().BeFalse();

        for (var ordinary = 0; ordinary < 5; ordinary++)
        {
            using var later = new SqliteConnection(PooledString(path));
            later.Open();
            HasMarker(later, 1).Should().BeFalse();
            HasMarker(later, 2).Should().BeFalse();
        }

        WriteEvidence("lease-retirement.json", new
        {
            operation = "lease.recover/BEGIN IMMEDIATE",
            recovered,
            attempts = observations.Count,
            pooling = observations.Select(item => item.Pooling).ToArray(),
            foreignKeys = observations.Select(item => item.ForeignKeys).ToArray(),
            busyTimeout = observations.Select(item => item.BusyTimeout).ToArray(),
            sawEarlierMarker = observations.Select(item => item.SawEarlierMarker).ToArray(),
            nativeAddressReuseObserved = NativeAddressWasReused(observations),
            ordinaryOpensWithoutFailedMarker = 5
        });
    }

    [Fact]
    public async Task Multiple_consecutive_classified_failures_use_a_new_physical_connection_each_time()
    {
        var path = Path.Combine(root, "multi.db");
        var observations = new List<Observation>();
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
            (_, attempt) => attempt <= 3 ? MappedFailure() : null,
            (connection, attempt) => observations.Add(Observe(connection, attempt)));

        var store = Store(path);
        using (new Scope(runtime))
            await store.InitializeAsync();

        observations.Should().HaveCount(4);
        observations.Should().OnlyContain(item => !item.Pooling);
        observations.Skip(1).Should().OnlyContain(item => !item.SawEarlierMarker);
        delays.Should().Equal(
            TimeSpan.FromMilliseconds(25),
            TimeSpan.FromMilliseconds(50),
            TimeSpan.FromMilliseconds(100));

        using var later = new SqliteConnection(PooledString(path));
        later.Open();
        for (var attempt = 1; attempt <= 4; attempt++)
            HasMarker(later, attempt).Should().BeFalse();

        WriteEvidence("multiple-failures.json", new
        {
            attempts = observations.Count,
            pooling = observations.Select(item => item.Pooling).ToArray(),
            sawEarlierMarker = observations.Select(item => item.SawEarlierMarker).ToArray(),
            nativeAddressReuseObserved = NativeAddressWasReused(observations),
            delaysMs = delays.Select(delay => delay.TotalMilliseconds).ToArray()
        });
    }

    [Fact]
    public async Task Retry_exhaustion_retires_every_attempt_connection()
    {
        var path = Path.Combine(root, "exhaustion.db");
        var observations = new List<Observation>();
        var runtime = new SqliteRecoveryRuntime(
            new FakeReader(),
            (_, _) => ValueTask.CompletedTask,
            TimeProvider.System,
            isWindows: true,
            (_, _) => MappedFailure(),
            (connection, attempt) => observations.Add(Observe(connection, attempt)));

        var store = Store(path);
        using (new Scope(runtime))
        {
            Func<Task> act = () => store.InitializeAsync().AsTask();
            var wrapper = (await act.Should().ThrowAsync<WorkflowPersistenceException>()).Which;
            var exception = wrapper.InnerException.Should().BeOfType<SqliteException>().Subject;
            exception.SqliteErrorCode.Should().Be(10);
            exception.SqliteExtendedErrorCode.Should().Be(1546);
            exception.Data[SqliteTransientRecovery.AttemptsKey].Should().Be(6);
            exception.Data[SqliteTransientRecovery.ReasonKey].Should().Be("attempt-limit");
        }

        observations.Should().HaveCount(6);
        observations.Should().OnlyContain(item => !item.Pooling);
        observations.Skip(1).Should().OnlyContain(item => !item.SawEarlierMarker);

        using var later = new SqliteConnection(PooledString(path));
        later.Open();
        for (var attempt = 1; attempt <= 6; attempt++)
            HasMarker(later, attempt).Should().BeFalse();

        WriteEvidence("exhaustion-retirement.json", new
        {
            attempts = observations.Count,
            pooling = observations.Select(item => item.Pooling).ToArray(),
            sawEarlierMarker = observations.Select(item => item.SawEarlierMarker).ToArray(),
            nativeAddressReuseObserved = NativeAddressWasReused(observations),
            terminalReason = "attempt-limit"
        });
    }

    [Fact]
    public async Task Cancellation_during_backoff_retires_the_failed_connection()
    {
        var path = Path.Combine(root, "cancellation.db");
        var observations = new List<Observation>();
        using var cancellation = new CancellationTokenSource();
        var runtime = new SqliteRecoveryRuntime(
            new FakeReader(),
            (_, token) =>
            {
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
                return ValueTask.CompletedTask;
            },
            TimeProvider.System,
            isWindows: true,
            (_, attempt) => attempt == 1 ? MappedFailure() : null,
            (connection, attempt) => observations.Add(Observe(connection, attempt)));

        var store = Store(path);
        using (new Scope(runtime))
        {
            Func<Task> act = () => store.InitializeAsync(cancellation.Token).AsTask();
            await act.Should().ThrowAsync<OperationCanceledException>();
        }

        observations.Should().HaveCount(1);
        observations[0].Pooling.Should().BeFalse();

        using var later = new SqliteConnection(PooledString(path));
        later.Open();
        HasMarker(later, 1).Should().BeFalse();

        WriteEvidence("cancellation-retirement.json", new
        {
            attempts = observations.Count,
            pooling = observations.Select(item => item.Pooling).ToArray(),
            failedMarkerSurvived = false
        });
    }

    [Fact]
    public async Task Ineligible_failure_is_refused_without_retry_and_the_connection_is_retired()
    {
        var path = Path.Combine(root, "ineligible.db");
        var observations = new List<Observation>();
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
            (_, attempt) => attempt == 1
                ? new SqliteException("SQLite Error 5: 'database is locked'.", 5, 5)
                : null,
            (connection, attempt) => observations.Add(Observe(connection, attempt)));

        var store = Store(path);
        using (new Scope(runtime))
        {
            Func<Task> act = () => store.InitializeAsync().AsTask();
            var wrapper = (await act.Should().ThrowAsync<WorkflowPersistenceException>()).Which;
            var exception = wrapper.InnerException.Should().BeOfType<SqliteException>().Subject;
            exception.SqliteErrorCode.Should().Be(5);
            exception.SqliteExtendedErrorCode.Should().Be(5);
        }

        observations.Should().HaveCount(1);
        observations[0].Pooling.Should().BeFalse();
        delays.Should().BeEmpty();

        using var later = new SqliteConnection(PooledString(path));
        later.Open();
        HasMarker(later, 1).Should().BeFalse();

        WriteEvidence("ineligible-retirement.json", new
        {
            attempts = observations.Count,
            retried = false,
            failedMarkerSurvived = false
        });
    }

    [Fact]
    public async Task Non_windows_keeps_the_pooled_first_attempt_and_does_not_retry()
    {
        var path = Path.Combine(root, "non-windows.db");
        var observations = new List<Observation>();
        var runtime = new SqliteRecoveryRuntime(
            new FakeReader(),
            (_, _) => ValueTask.CompletedTask,
            TimeProvider.System,
            isWindows: false,
            (_, attempt) => attempt == 1 ? MappedFailure() : null,
            (connection, attempt) => observations.Add(Observe(connection, attempt)));

        var store = Store(path);
        using (new Scope(runtime))
        {
            Func<Task> act = () => store.InitializeAsync().AsTask();
            var wrapper = (await act.Should().ThrowAsync<WorkflowPersistenceException>()).Which;
            wrapper.InnerException.Should().BeOfType<SqliteException>();
        }

        observations.Should().HaveCount(1);
        observations[0].Pooling.Should().BeTrue(
            "non-Windows behavior must keep the configured pooled connection ownership");

        using var later = new SqliteConnection(PooledString(path));
        later.Open();
        HasMarker(later, 1).Should().BeTrue(
            "the non-Windows path still returns its connection to the pool, unchanged");

        WriteEvidence("non-windows-unchanged.json", new
        {
            attempts = observations.Count,
            firstAttemptPooling = observations[0].Pooling,
            retried = false,
            pooledMarkerSurvived = true
        });
    }

    [Fact]
    public async Task Concurrent_unrelated_pooled_connection_is_preserved()
    {
        var path = Path.Combine(root, "concurrent.db");

        var first = Store(path);
        await first.InitializeAsync();

        using var unrelated = new SqliteConnection(PooledString(path));
        unrelated.Open();
        CreateMarker(unrelated, 50);

        var observations = new List<Observation>();
        var runtime = new SqliteRecoveryRuntime(
            new FakeReader(),
            (_, _) => ValueTask.CompletedTask,
            TimeProvider.System,
            isWindows: true,
            (operation, attempt) =>
                operation == SqliteRecoveryOperation.SchemaVerify && attempt == 1
                    ? MappedFailure()
                    : null,
            (connection, attempt) => observations.Add(Observe(connection, attempt)));

        var second = Store(path);
        using (new Scope(runtime))
            await second.InitializeAsync();

        observations.Should().HaveCount(2);
        observations.Should().OnlyContain(item => !item.Pooling);

        HasMarker(unrelated, 50).Should().BeTrue(
            "an unrelated active pooled connection must not be cleared or retired");
        using (var command = unrelated.CreateCommand())
        {
            command.CommandText = "SELECT 1;";
            Convert.ToInt32(command.ExecuteScalar()).Should().Be(1);
        }

        WriteEvidence("concurrent-preserved.json", new
        {
            recoveryAttempts = observations.Count,
            unrelatedConnectionMarkerSurvived = true,
            unrelatedConnectionUsable = true
        });
    }

    [Fact]
    public async Task Retired_failure_leaves_no_physical_handle_leak()
    {
        var leakRoot = Path.Combine(root, "leak");
        Directory.CreateDirectory(leakRoot);
        var path = Path.Combine(leakRoot, "leak.db");

        var observations = new List<Observation>();
        var runtime = new SqliteRecoveryRuntime(
            new FakeReader(),
            (_, _) => ValueTask.CompletedTask,
            TimeProvider.System,
            isWindows: true,
            (operation, attempt) =>
                operation == SqliteRecoveryOperation.SchemaVerify && attempt == 1
                    ? MappedFailure()
                    : null,
            (connection, attempt) => observations.Add(Observe(connection, attempt)));

        var store = Store(path);
        using (new Scope(runtime))
            await store.InitializeAsync();

        observations.Should().HaveCount(2);
        observations.Should().OnlyContain(item => !item.Pooling);

        SqliteConnection.ClearAllPools();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Directory.Delete(leakRoot, recursive: true);

        WriteEvidence("no-handle-leak.json", new
        {
            attempts = observations.Count,
            databaseDirectoryDeleted = true
        });
    }
}

#pragma warning restore xUnit1051
