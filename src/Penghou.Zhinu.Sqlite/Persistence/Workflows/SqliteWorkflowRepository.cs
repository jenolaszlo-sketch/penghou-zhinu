using Microsoft.Data.Sqlite;
using System.Text.Json;
using Penghou.Zhinu.Sqlite.Persistence.Steps;

namespace Penghou.Zhinu.Sqlite.Persistence.Workflows;

/// <summary>Coordinates workflow-run and event commands and queries.</summary>
internal sealed class SqliteWorkflowRepository : IWorkflowRepository
{
    private readonly IZhinuSqliteDatabase factory;
    private readonly InsertRunCommand insertRun = new();
    private readonly InsertEventCommand insertEvent = new();
    private readonly UpdateRunMetadataCommand updateRunMetadata = new();
    private readonly AppendEventCommand appendEvent = new();
    private readonly FinishRunCommand finishRun = new();
    private readonly CancelRunCommand cancelRun = new();
    private readonly CancelRunStepsCommand cancelRunSteps = new();
    private readonly PurgeRunsCommand purgeRuns = new();
    private readonly GetRunQuery getRun = new();
    private readonly GetRunsQuery getRuns = new();
    private readonly GetRunSubtreeQuery getRunSubtree = new();
    private readonly GetEventsQuery getEvents = new();

    public SqliteWorkflowRepository(IZhinuSqliteDatabase factory) => this.factory = factory;

    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default) =>
        await factory.InitializeAsync(cancellationToken).ConfigureAwait(false);

    public async ValueTask CreateRunAsync(
        WorkflowRun run,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        await factory.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await factory.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        await insertRun.ExecuteAsync(connection, transaction, run, cancellationToken)
            .ConfigureAwait(false);
        await insertEvent.ExecuteAsync(
            connection,
            transaction,
            run.Id,
            null,
            WorkflowEventTypes.WorkflowStarted,
            run.CreatedAt,
            null,
            null,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<WorkflowRun?> GetRunAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty)
            throw new ArgumentException("Workflow ID must not be empty.", nameof(id));
        await factory.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await factory.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        return await getRun.ExecuteAsync(connection, null, id, cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<WorkflowRun>> GetRunsAsync(
        RunQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        query.Validate();
        await factory.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await factory.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        return await getRuns.ExecuteAsync(connection, query, cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<WorkflowRun?> UpdateRunMetadataAsync(
        Guid workflowRunId,
        string? metadataJson,
        CancellationToken cancellationToken = default)
    {
        if (workflowRunId == Guid.Empty)
            throw new ArgumentException("Workflow ID must not be empty.", nameof(workflowRunId));
        await factory.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await factory.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        if (await updateRunMetadata.ExecuteAsync(
            connection,
            transaction,
            workflowRunId,
            metadataJson,
            DateTimeOffset.UtcNow,
            cancellationToken).ConfigureAwait(false) != 1)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }
        var run = await getRun.ExecuteAsync(
            connection,
            transaction,
            workflowRunId,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return run;
    }

    public async ValueTask<IReadOnlyList<WorkflowRun>> GetRunSubtreeAsync(
        Guid workflowRunId,
        int maxDepth,
        CancellationToken cancellationToken = default)
    {
        if (workflowRunId == Guid.Empty)
            throw new ArgumentException("Workflow ID must not be empty.", nameof(workflowRunId));
        if (maxDepth < 0)
            throw new ArgumentOutOfRangeException(nameof(maxDepth));
        await factory.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await factory.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        return await getRunSubtree.ExecuteAsync(
            connection,
            workflowRunId,
            maxDepth,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<WorkflowEvent>> GetEventsAsync(
        Guid workflowRunId,
        long afterSequence,
        int limit,
        CancellationToken cancellationToken = default)
    {
        await factory.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await factory.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        return await getEvents.ExecuteAsync(
            connection,
            workflowRunId,
            afterSequence,
            limit,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<WorkflowEvent> AppendEventAsync(
        Guid workflowRunId,
        string eventType,
        string? dataJson,
        string? stepKey = null,
        int? attempt = null,
        CancellationToken cancellationToken = default)
    {
        if (workflowRunId == Guid.Empty)
            throw new ArgumentException("Workflow ID must not be empty.", nameof(workflowRunId));
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);
        await factory.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await factory.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        return await appendEvent.ExecuteAsync(
            connection,
            workflowRunId,
            eventType,
            dataJson,
            stepKey,
            attempt,
            DateTimeOffset.UtcNow,
            cancellationToken).ConfigureAwait(false);
    }

    public ValueTask CompleteRunAsync(
        Guid workflowRunId,
        string ownerId,
        string? outputJson,
        string outputType,
        DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        FinishRunAsync(
            workflowRunId,
            ownerId,
            WorkflowStatus.Completed,
            outputJson,
            outputType,
            null,
            WorkflowEventTypes.WorkflowCompleted,
            now,
            cancellationToken);

    public ValueTask FailRunAsync(
        Guid workflowRunId,
        string ownerId,
        WorkflowError error,
        DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        FinishRunAsync(
            workflowRunId,
            ownerId,
            WorkflowStatus.Failed,
            null,
            string.Empty,
            error,
            WorkflowEventTypes.WorkflowFailed,
            now,
            cancellationToken);

    public async ValueTask CancelRunAsync(
        Guid workflowRunId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await factory.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await factory.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var run = await getRun.ExecuteAsync(
            connection,
            transaction,
            workflowRunId,
            cancellationToken).ConfigureAwait(false);
        if (run is not null &&
            run.Status is WorkflowStatus.Pending or WorkflowStatus.Running)
        {
            RunStateMachine.AssertCanTransition(run.Status, WorkflowStatus.Cancelled, workflowRunId);
        }
        if (await cancelRun.ExecuteAsync(
            connection,
            transaction,
            workflowRunId,
            now,
            cancellationToken).ConfigureAwait(false) == 0)
        {
            return;
        }
        await cancelRunSteps.ExecuteAsync(
            connection,
            transaction,
            workflowRunId,
            now,
            cancellationToken).ConfigureAwait(false);
        await insertEvent.ExecuteAsync(
            connection,
            transaction,
            workflowRunId,
            null,
            WorkflowEventTypes.WorkflowCancelled,
            now,
            null,
            null,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<int> PurgeRunsAsync(
        DateTimeOffset olderThan,
        IReadOnlyList<WorkflowStatus>? statuses = null,
        CancellationToken cancellationToken = default)
    {
        await factory.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await factory.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var deleted = await purgeRuns.ExecuteAsync(
            connection,
            transaction,
            olderThan,
            statuses,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return deleted;
    }

    private async ValueTask FinishRunAsync(
        Guid workflowRunId,
        string ownerId,
        WorkflowStatus status,
        string? outputJson,
        string outputType,
        WorkflowError? error,
        string eventType,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await factory.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await factory.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var run = await getRun.ExecuteAsync(
            connection,
            transaction,
            workflowRunId,
            cancellationToken).ConfigureAwait(false);
        if (run is not null)
        {
            RunStateMachine.AssertCanTransition(run.Status, status, workflowRunId);
        }
        if (await finishRun.ExecuteAsync(
            connection,
            transaction,
            workflowRunId,
            ownerId,
            status,
            outputJson,
            outputType,
            error,
            now,
            cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new WorkflowStateException("Workflow completion requires an owned running lease.");
        }
        await insertEvent.ExecuteAsync(
            connection,
            transaction,
            workflowRunId,
            null,
            eventType,
            now,
            null,
            error is null
                ? null
                : JsonSerializer.Serialize(error, SqliteStoreSupport.SerializerOptions),
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}
