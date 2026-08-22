using Microsoft.Data.Sqlite;
using System.Text.Json;
using Penghou.Zhinu.Sqlite.Persistence.Workflows;

namespace Penghou.Zhinu.Sqlite.Persistence.Steps;

/// <summary>
/// Applies a durable step completion or delay transition (shared by step
/// completion and timer scheduling): verifies the owned running lease, records
/// the committed result into the compensation row, and emits the event.
/// </summary>
internal sealed class SqliteStepFinisher
{
    private readonly IZhinuSqliteDatabase factory;
    private readonly GetStepByIdQuery getStepById = new();
    private readonly FinishStepCommand finishStep = new();
    private readonly RecordCompensationInputCommand recordCompensationInput = new();
    private readonly InsertEventCommand insertEvent = new();

    public SqliteStepFinisher(IZhinuSqliteDatabase factory) => this.factory = factory;

    public async ValueTask FinishStepAsync(
        Guid stepId,
        string ownerId,
        StepStatus status,
        string? outputJson,
        WorkflowError? error,
        DateTimeOffset? availableAt,
        string eventType,
        DateTimeOffset now,
        CancellationToken cancellationToken)
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
        StepStateMachine.AssertCanTransition(step.Status, status, stepId);
        if (await finishStep.ExecuteAsync(
            connection,
            transaction,
            stepId,
            ownerId,
            status,
            outputJson,
            error,
            availableAt,
            now,
            cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new WorkflowStateException("Step transition requires an owned running lease.");
        }
        if (status == StepStatus.Completed)
        {
            await recordCompensationInput.ExecuteAsync(
                connection,
                transaction,
                step.WorkflowRunId,
                step.StepKey,
                step.Revision,
                outputJson,
                step.OutputType,
                cancellationToken).ConfigureAwait(false);
        }
        await insertEvent.ExecuteAsync(
            connection,
            transaction,
            step.WorkflowRunId,
            step.StepKey,
            eventType,
            now,
            step.Attempt,
            error is null
                ? null
                : JsonSerializer.Serialize(error, SqliteStoreSupport.SerializerOptions),
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}
