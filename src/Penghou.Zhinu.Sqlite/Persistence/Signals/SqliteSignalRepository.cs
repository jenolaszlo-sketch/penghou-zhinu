using Microsoft.Data.Sqlite;
using System.Text.Json;
using Penghou.Zhinu.Sqlite.Persistence.Steps;
using Penghou.Zhinu.Sqlite.Persistence.Workflows;

namespace Penghou.Zhinu.Sqlite.Persistence.Signals;

/// <summary>Coordinates signal buffering and delivery to waiting steps.</summary>
internal sealed class SqliteSignalRepository : IWorkflowSignalRepository
{
    private readonly SqliteConnectionFactory factory;
    private readonly GetRunStatusQuery getRunStatus = new();
    private readonly InsertSignalCommand insertSignal = new();
    private readonly InsertEventCommand insertEvent = new();
    private readonly GetStepByIdQuery getStepById = new();
    private readonly GetUndeliveredSignalQuery getUndeliveredSignal = new();
    private readonly TransitionStepToWaitingCommand transitionStepToWaiting = new();
    private readonly MarkSignalDeliveredCommand markSignalDelivered = new();
    private readonly CompleteStepWithSignalCommand completeStepWithSignal = new();

    public SqliteSignalRepository(SqliteConnectionFactory factory) => this.factory = factory;

    public async ValueTask SendSignalAsync(
        Guid workflowRunId,
        string signalName,
        string? dataJson,
        CancellationToken cancellationToken = default)
    {
        if (workflowRunId == Guid.Empty)
            throw new ArgumentException("Workflow ID must not be empty.", nameof(workflowRunId));
        ArgumentException.ThrowIfNullOrWhiteSpace(signalName);
        await factory.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await factory.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var runStatus = await getRunStatus.ExecuteAsync(
            connection,
            transaction,
            workflowRunId,
            cancellationToken).ConfigureAwait(false);
        if (runStatus is null)
        {
            throw new KeyNotFoundException(
                $"Workflow '{workflowRunId:D}' does not exist.");
        }
        var now = DateTimeOffset.UtcNow;
        await insertSignal.ExecuteAsync(
            connection,
            transaction,
            workflowRunId,
            signalName,
            dataJson,
            now,
            cancellationToken).ConfigureAwait(false);
        await insertEvent.ExecuteAsync(
            connection,
            transaction,
            workflowRunId,
            null,
            WorkflowEventTypes.SignalSent,
            now,
            null,
            JsonSerializer.Serialize(
                new { signalName, data = dataJson },
                SqliteStoreSupport.SerializerOptions),
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<SignalDelivery?> TryDeliverSignalAsync(
        Guid stepId,
        string ownerId,
        string signalName,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(signalName);
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
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new SignalDelivery(step.OutputJson);
        }
        if (step.SignalName is not null &&
            !string.Equals(step.SignalName, signalName, StringComparison.Ordinal))
        {
            throw new WorkflowStateException(
                $"Step '{step.StepKey}' is waiting on signal '{step.SignalName}', not '{signalName}'.");
        }
        if (step.Status is not (StepStatus.Waiting or StepStatus.Running))
            return null;
        var effectiveName = step.SignalName ?? signalName;
        var (signalId, dataJson) = await getUndeliveredSignal.ExecuteAsync(
            connection,
            transaction,
            step.WorkflowRunId,
            effectiveName,
            cancellationToken).ConfigureAwait(false);
        if (signalId is null)
        {
            if (step.Status == StepStatus.Running)
            {
                await transitionStepToWaiting.ExecuteAsync(
                    connection,
                    transaction,
                    stepId,
                    effectiveName,
                    cancellationToken).ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }
        if (await markSignalDelivered.ExecuteAsync(
            connection,
            transaction,
            signalId,
            stepId,
            cancellationToken).ConfigureAwait(false) != 1)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }
        await completeStepWithSignal.ExecuteAsync(
            connection,
            transaction,
            stepId,
            dataJson,
            effectiveName,
            now,
            cancellationToken).ConfigureAwait(false);
        await insertEvent.ExecuteAsync(
            connection,
            transaction,
            step.WorkflowRunId,
            step.StepKey,
            WorkflowEventTypes.SignalDelivered,
            now,
            step.Attempt,
            JsonSerializer.Serialize(
                new { signalName = effectiveName, data = dataJson },
                SqliteStoreSupport.SerializerOptions),
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new SignalDelivery(dataJson);
    }
}
