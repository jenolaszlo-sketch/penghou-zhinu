using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Microsoft.Data.Sqlite;

namespace Penghou.Zhinu.Sqlite.Recovery;

/// <summary>
/// Bounded recovery for the Windows WAL-index mapped-section race
/// (<c>SQLITE_IOERR_TRUNCATE</c> plus <c>ERROR_USER_MAPPED_FILE</c>).
///
/// Only two pre-transaction operations may enter this loop: the read-only
/// schema compatibility verification and the lease-recovery
/// <c>BEGIN IMMEDIATE</c>. Once <c>BEGIN</c> succeeds the helper returns and no
/// later failure can be retried here.
/// </summary>
internal static class SqliteTransientRecovery
{
    internal const int MaximumAttempts = 6;
    internal static readonly TimeSpan SchedulingBudget = TimeSpan.FromSeconds(2);

    internal const string OperationKey = "zhinu.sqlite.recovery.operation";
    internal const string AttemptsKey = "zhinu.sqlite.recovery.attempts";
    internal const string ElapsedMillisecondsKey = "zhinu.sqlite.recovery.elapsed_ms";
    internal const string PrimaryKey = "zhinu.sqlite.recovery.primary";
    internal const string ExtendedKey = "zhinu.sqlite.recovery.extended";
    internal const string SystemErrnoKey = "zhinu.sqlite.recovery.system_errno";
    internal const string ReasonKey = "zhinu.sqlite.recovery.reason";
    internal const string FirstFailureKey = "zhinu.sqlite.recovery.first_failure";
    internal const string DisposeFailureKey = "zhinu.sqlite.recovery.dispose_failure";
    internal const string ReopenFailureKey = "zhinu.sqlite.recovery.reopen_failure";

    private const int PrimarySqliteIoError = 10;
    private const int ExtendedSqliteIoErrorTruncate = 1546;
    private const int SystemErrorUserMappedFile = 1224;

    private static readonly TimeSpan[] Backoff =
    {
        TimeSpan.FromMilliseconds(25),
        TimeSpan.FromMilliseconds(50),
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(200),
        TimeSpan.FromMilliseconds(400)
    };

    internal static ValueTask<(SqliteConnection Connection, int Version)> ExecuteSchemaVerifyAsync(
        IZhinuSqliteDatabase factory,
        Func<SqliteConnection, CancellationToken, ValueTask<int>> verify,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            factory,
            SqliteRecoveryOperation.SchemaVerify,
            verify,
            cancellationToken);

    internal static ValueTask<(SqliteConnection Connection, SqliteTransaction Transaction)> ExecuteLeaseBeginAsync(
        IZhinuSqliteDatabase factory,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            factory,
            SqliteRecoveryOperation.LeaseRecoverBegin,
            static (connection, _) => ValueTask.FromResult(
                connection.BeginTransaction(deferred: false)),
            cancellationToken);

    private static async ValueTask<(SqliteConnection Connection, TResult Result)> ExecuteAsync<TResult>(
        IZhinuSqliteDatabase factory,
        SqliteRecoveryOperation operation,
        Func<SqliteConnection, CancellationToken, ValueTask<TResult>> attempt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(attempt);
        if (operation == SqliteRecoveryOperation.None)
            throw new ArgumentOutOfRangeException(nameof(operation));

        var runtime = SqliteRecoveryTestHooks.Runtime.Value ?? SqliteRecoveryRuntime.Default;
        var recovery = factory as IZhinuSqliteDatabaseRecovery;
        SqliteException? firstEligible = null;
        long? retryStarted = null;
        SqliteNativeErrorState lastState = default;

        for (var attemptNumber = 1; attemptNumber <= MaximumAttempts; attemptNumber++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            SqliteConnection connection;
            try
            {
                // On Windows the recovery-owned operations must own the physical
                // connection from the first attempt. A pooled first attempt would
                // return the failed physical connection to the pool when disposed,
                // and a later ordinary open could silently reacquire it. A
                // non-pooled connection is closed physically on disposal, so it
                // cannot be reacquired by a retry or by any later operation.
                //
                // Non-Windows and custom owners keep their existing connection
                // ownership: they cannot classify an eligible failure, and this
                // correction must not change their behaviour.
                if (recovery is not null && runtime.IsWindows)
                    connection = await recovery.OpenFreshAsync(cancellationToken).ConfigureAwait(false);
                else
                    connection = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception openFailure)
            {
                if (firstEligible is null)
                    throw;

                firstEligible.Data[ReopenFailureKey] = openFailure;
                ExceptionDispatchInfo.Capture(firstEligible).Throw();
                throw;
            }

            try
            {
                runtime.ConnectionOpened?.Invoke(connection, attemptNumber);
                var injected = runtime.AttemptFailureInjector?.Invoke(operation, attemptNumber);
                if (injected is not null)
                    throw injected;

                var result = await attempt(connection, cancellationToken).ConfigureAwait(false);
                if (attemptNumber > 1)
                    RecordOutcome(operation, "succeeded", lastState);

                return (connection, result);
            }
            catch (SqliteException exception)
            {
                var stateAvailable = runtime.NativeErrorStateReader.TryRead(connection, out var state);
                var eligible = IsEligible(
                    runtime, operation, exception, stateAvailable, state, cancellationToken);

                if (!eligible)
                {
                    if (stateAvailable)
                        RecordOutcome(operation, "refused", state);
                    if (firstEligible is not null && !ReferenceEquals(firstEligible, exception))
                        exception.Data[FirstFailureKey] = firstEligible;
                    var disposeFailure = await TryDisposeAsync(connection).ConfigureAwait(false);
                    if (disposeFailure is not null)
                        exception.Data[DisposeFailureKey] = disposeFailure;
                    throw;
                }

                firstEligible ??= exception;
                retryStarted ??= runtime.TimeProvider.GetTimestamp();
                lastState = state;
                var elapsed = runtime.TimeProvider.GetElapsedTime(retryStarted.Value);

                if (recovery is null)
                {
                    AttachDiagnostics(
                        exception, operation, attemptNumber, elapsed, state, firstEligible,
                        "recovery-capability-missing");
                    RecordOutcome(operation, "refused", state);
                    await AttachDisposeFailureAsync(connection, exception).ConfigureAwait(false);
                    throw;
                }

                if (attemptNumber >= MaximumAttempts)
                {
                    AttachDiagnostics(
                        exception, operation, attemptNumber, elapsed, state, firstEligible,
                        "attempt-limit");
                    RecordOutcome(operation, "exhausted", state);
                    await AttachDisposeFailureAsync(connection, exception).ConfigureAwait(false);
                    throw;
                }

                var delay = Backoff[Math.Min(attemptNumber - 1, Backoff.Length - 1)];
                if (elapsed + delay > SchedulingBudget)
                {
                    AttachDiagnostics(
                        exception, operation, attemptNumber, elapsed, state, firstEligible,
                        "budget");
                    RecordOutcome(operation, "exhausted", state);
                    await AttachDisposeFailureAsync(connection, exception).ConfigureAwait(false);
                    throw;
                }

                var retryDisposeFailure = await TryDisposeAsync(connection).ConfigureAwait(false);
                if (retryDisposeFailure is not null)
                {
                    exception.Data[DisposeFailureKey] = retryDisposeFailure;
                    AttachDiagnostics(
                        exception, operation, attemptNumber, elapsed, state, firstEligible,
                        "dispose-failure");
                    RecordOutcome(operation, "exhausted", state);
                    throw;
                }

                RecordOutcome(operation, "scheduled", state);
                cancellationToken.ThrowIfCancellationRequested();
                await runtime.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                if (firstEligible is not null && !ReferenceEquals(firstEligible, exception))
                    exception.Data[FirstFailureKey] = firstEligible;
                var disposeFailure = await TryDisposeAsync(connection).ConfigureAwait(false);
                if (disposeFailure is not null)
                    exception.Data[DisposeFailureKey] = disposeFailure;
                throw;
            }
        }

        throw new InvalidOperationException("Recovery retry loop exited without a result.");
    }

    private static bool IsEligible(
        SqliteRecoveryRuntime runtime,
        SqliteRecoveryOperation operation,
        SqliteException exception,
        bool stateAvailable,
        SqliteNativeErrorState state,
        CancellationToken cancellationToken)
    {
        if (!runtime.IsWindows || cancellationToken.IsCancellationRequested)
            return false;

        if (operation is not (SqliteRecoveryOperation.SchemaVerify
            or SqliteRecoveryOperation.LeaseRecoverBegin))
            return false;

        if (exception.SqliteErrorCode != PrimarySqliteIoError ||
            exception.SqliteExtendedErrorCode != ExtendedSqliteIoErrorTruncate)
            return false;

        if (!stateAvailable)
            return false;

        if (state.PrimaryCode != PrimarySqliteIoError ||
            state.ExtendedCode != ExtendedSqliteIoErrorTruncate ||
            state.SystemErrno != SystemErrorUserMappedFile)
            return false;

        // Missing, stale or contradictory native state makes the failure
        // ineligible. Never infer the errno from the extended code alone.
        return state.PrimaryCode == exception.SqliteErrorCode &&
               state.ExtendedCode == exception.SqliteExtendedErrorCode;
    }

    private static async ValueTask<Exception?> TryDisposeAsync(SqliteConnection connection)
    {
        try
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            return null;
        }
        catch (Exception disposeFailure)
        {
            return disposeFailure;
        }
    }

    private static async ValueTask AttachDisposeFailureAsync(
        SqliteConnection connection,
        SqliteException exception)
    {
        var disposeFailure = await TryDisposeAsync(connection).ConfigureAwait(false);
        if (disposeFailure is not null)
            exception.Data[DisposeFailureKey] = disposeFailure;
    }

    private static void AttachDiagnostics(
        SqliteException exception,
        SqliteRecoveryOperation operation,
        int attempts,
        TimeSpan elapsed,
        SqliteNativeErrorState state,
        SqliteException firstEligible,
        string reason)
    {
        exception.Data[OperationKey] = operation.ToString();
        exception.Data[AttemptsKey] = attempts;
        exception.Data[ElapsedMillisecondsKey] = (long)elapsed.TotalMilliseconds;
        exception.Data[PrimaryKey] = state.PrimaryCode;
        exception.Data[ExtendedKey] = state.ExtendedCode;
        exception.Data[SystemErrnoKey] = state.SystemErrno;
        exception.Data[ReasonKey] = reason;
        if (!ReferenceEquals(firstEligible, exception))
            exception.Data[FirstFailureKey] = firstEligible;
    }

    private static void RecordOutcome(
        SqliteRecoveryOperation operation,
        string outcome,
        SqliteNativeErrorState state)
    {
        ZhinuSqliteDiagnostics.RecoveryAttempts.Add(
            1,
            new KeyValuePair<string, object?>("operation", operation.ToString()),
            new KeyValuePair<string, object?>("outcome", outcome),
            new KeyValuePair<string, object?>("primary", state.PrimaryCode),
            new KeyValuePair<string, object?>("extended", state.ExtendedCode),
            new KeyValuePair<string, object?>("system_errno", state.SystemErrno));

        if (Activity.Current is { } activity)
        {
            activity.AddEvent(new ActivityEvent(
                "zhinu.sqlite.recovery",
                tags: new ActivityTagsCollection
                {
                    { "operation", operation.ToString() },
                    { "outcome", outcome },
                    { "primary", state.PrimaryCode },
                    { "extended", state.ExtendedCode },
                    { "system_errno", state.SystemErrno }
                }));
        }
    }
}
