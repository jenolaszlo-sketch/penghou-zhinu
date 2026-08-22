using Microsoft.Data.Sqlite;
using Penghou.Zhinu.Sqlite.Persistence.Steps;
using Penghou.Zhinu.Sqlite.Persistence.Workflows;

namespace Penghou.Zhinu.Sqlite.Persistence.Timers;

/// <summary>Coordinates delayed step scheduling (timers).</summary>
internal sealed class SqliteTimerRepository : IWorkflowTimerRepository
{
    private readonly IZhinuSqliteDatabase factory;
    private readonly SqliteStepFinisher stepFinisher;
    private readonly GetStepByIdQuery getStepById = new();
    private readonly CompleteDelayCommand completeDelay = new();
    private readonly InsertEventCommand insertEvent = new();

    public SqliteTimerRepository(IZhinuSqliteDatabase factory)
    {
        this.factory = factory;
        stepFinisher = new(factory);
    }

    public async ValueTask ScheduleDelayAsync(
        Guid stepId,
        string ownerId,
        DateTimeOffset availableAt,
        DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        await stepFinisher.FinishStepAsync(
            stepId,
            ownerId,
            StepStatus.Waiting,
            null,
            null,
            availableAt,
            WorkflowEventTypes.DelayScheduled,
            now,
            cancellationToken).ConfigureAwait(false);

    public async ValueTask CompleteDelayAsync(
        Guid stepId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await factory.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await factory.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var step = await getStepById.ExecuteAsync(
            connection,
            transaction,
            stepId,
            cancellationToken).ConfigureAwait(false) ??
            throw new KeyNotFoundException($"Step '{stepId:D}' does not exist.");
        if (step.Status == StepStatus.Completed)
            return;
        if (step.Status != StepStatus.Waiting || step.AvailableAt > now)
            throw new WorkflowStateException("Durable delay is not eligible to complete.");
        if (await completeDelay.ExecuteAsync(
            connection,
            transaction,
            stepId,
            now,
            cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new WorkflowStateException("Durable delay completion lost its state transition.");
        }
        await insertEvent.ExecuteAsync(
            connection,
            transaction,
            step.WorkflowRunId,
            step.StepKey,
            WorkflowEventTypes.StepCompleted,
            now,
            step.Attempt,
            null,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}
