using Microsoft.Data.Sqlite;
using System.Text.Json;
using Penghou.Zhinu.Sqlite.Persistence.Leases;
using Penghou.Zhinu.Sqlite.Persistence.Workflows;

namespace Penghou.Zhinu.Sqlite.Persistence.Steps;

internal sealed partial class SqliteStepRepository
{
    public async ValueTask<StepClaimResult> ClaimStepAsync(
        StepClaimRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateClaim(request);
        await factory.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await factory.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var runStatus = await getRunStatus.ExecuteAsync(
            connection,
            transaction,
            request.WorkflowRunId,
            cancellationToken).ConfigureAwait(false);
        if (runStatus == WorkflowStatus.Cancelled)
        {
            return new StepClaimResult(
                StepClaimDisposition.Cancelled,
                CancelledPlaceholder(request));
        }
        if (runStatus is not (WorkflowStatus.Pending or WorkflowStatus.Running))
        {
            throw new WorkflowStateException(
                $"Workflow '{request.WorkflowRunId:D}' is not executable in state '{runStatus}'.");
        }
        var runGeneration = await getRunLeaseGeneration.ExecuteAsync(
            connection,
            transaction,
            request.WorkflowRunId,
            cancellationToken).ConfigureAwait(false);
        if (request.LeaseGeneration != runGeneration)
        {
            ZhinuDiagnostics.FencingRejectionsCounter.Add(1);
            throw new LeaseLostException(
                $"Workflow '{request.WorkflowRunId:D}' lease generation {request.LeaseGeneration} " +
                $"no longer matches the current generation {runGeneration}.");
        }
        var boundGeneration = await getBoundGenerationStatus.ExecuteAsync(
            connection,
            transaction,
            request.WorkflowRunId,
            cancellationToken).ConfigureAwait(false);
        if (boundGeneration is not null &&
            boundGeneration != (int)WorkflowGenerationStatus.Active &&
            boundGeneration != (int)WorkflowGenerationStatus.Created &&
            boundGeneration != (int)WorkflowGenerationStatus.Prepared)
        {
            // The run is bound to a quiescing or superseded execution
            // generation: schedule no new work. Quiescing resumes via polling;
            // superseded ownership never returns. Created/prepared bindings
            // are admission in flight and schedule normally.
            var deferred = boundGeneration == (int)WorkflowGenerationStatus.Quiescing;
            ZhinuDiagnostics.FencingRejectionsCounter.Add(1);
            return new StepClaimResult(
                deferred
                    ? StepClaimDisposition.Deferred
                    : StepClaimDisposition.Superseded,
                UnscheduledPlaceholder(
                    request,
                    deferred ? StepStatus.Waiting : StepStatus.Cancelled));
        }
        var existing = await getStep.ExecuteAsync(
            connection,
            transaction,
            request.WorkflowRunId,
            request.StepKey,
            cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            var created = new WorkflowStepRun
            {
                Id = Guid.NewGuid(),
                WorkflowRunId = request.WorkflowRunId,
                StepKey = request.StepKey,
                ImplementationKey = request.ImplementationKey,
                Status = StepStatus.Running,
                Attempt = 1,
                CreatedAt = request.Now,
                StartedAt = request.Now,
                InputJson = request.InputJson,
                InputType = request.InputType,
                InputHash = request.InputHash,
                OutputType = request.OutputType,
                LeaseOwner = request.OwnerId,
                LeaseExpiresAt = request.LeaseExpiresAt,
                LeaseGeneration = request.LeaseGeneration
            };
            await insertStep.ExecuteAsync(connection, transaction, created, cancellationToken)
                .ConfigureAwait(false);
            await InsertDependenciesAsync(
                connection,
                transaction,
                request.WorkflowRunId,
                request.StepKey,
                request.DependsOn,
                request.Now,
                cancellationToken).ConfigureAwait(false);
            await InsertCompensationAsync(
                connection,
                transaction,
                request,
                created.Revision,
                request.LeaseGeneration,
                cancellationToken).ConfigureAwait(false);
            await insertEvent.ExecuteAsync(
                connection,
                transaction,
                request.WorkflowRunId,
                request.StepKey,
                WorkflowEventTypes.StepStarted,
                request.Now,
                1,
                null,
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new StepClaimResult(StepClaimDisposition.Acquired, created);
        }

        if (existing.Status == StepStatus.Completed &&
            !ContractMatches(existing, request) &&
            request.AllowSupersede)
        {
            // A forked run inherited this completed step from its source. The
            // destination definition supplied a different input, so the
            // inherited revision is obsolete: keep it as history and claim a
            // fresh pending revision that re-runs under the new contract.
            existing = await InsertSupersedingRevisionAsync(
                connection,
                transaction,
                existing,
                request,
                runGeneration,
                cancellationToken).ConfigureAwait(false);
        }
        ValidateStepContract(existing, request);
        if (existing.Status == StepStatus.Completed)
        {
            await insertEvent.ExecuteAsync(
                connection,
                transaction,
                request.WorkflowRunId,
                request.StepKey,
                WorkflowEventTypes.StepReused,
                request.Now,
                existing.Attempt,
                null,
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new StepClaimResult(StepClaimDisposition.Reused, existing);
        }
        if (existing.Status == StepStatus.Failed)
            return new StepClaimResult(StepClaimDisposition.Failed, existing);
        if (existing.Status == StepStatus.Cancelled)
            return new StepClaimResult(StepClaimDisposition.Cancelled, existing);
        if (existing.Status == StepStatus.Waiting &&
            (existing.SignalName is not null ||
             (existing.AvailableAt is not null &&
              existing.AvailableAt > request.Now)))
        {
            return new StepClaimResult(StepClaimDisposition.Waiting, existing);
        }
        if (existing.Status == StepStatus.Running &&
            existing.LeaseExpiresAt > request.Now)
        {
            return new StepClaimResult(StepClaimDisposition.Busy, existing);
        }

        var attempt = existing.Attempt < 1 ? 1 : existing.Attempt + 1;
        StepStateMachine.AssertCanTransition(existing.Status, StepStatus.Running, existing.Id);
        await claimStep.ExecuteAsync(
            connection,
            transaction,
            existing.Id,
            request.OwnerId,
            attempt,
            StepStatus.Running,
            request.Now,
            request.LeaseExpiresAt,
            request.LeaseGeneration,
            cancellationToken).ConfigureAwait(false);
        await InsertDependenciesAsync(
            connection,
            transaction,
            request.WorkflowRunId,
            request.StepKey,
            request.DependsOn,
            request.Now,
            cancellationToken).ConfigureAwait(false);
        await InsertCompensationAsync(
            connection,
            transaction,
            request,
            existing.Revision,
            request.LeaseGeneration,
            cancellationToken).ConfigureAwait(false);
        await insertEvent.ExecuteAsync(
            connection,
            transaction,
            request.WorkflowRunId,
            request.StepKey,
            WorkflowEventTypes.StepStarted,
            request.Now,
            attempt,
            null,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new StepClaimResult(
            StepClaimDisposition.Acquired,
            existing with
            {
                Status = StepStatus.Running,
                Attempt = attempt,
                StartedAt = request.Now,
                AvailableAt = null,
                Error = null,
                LeaseOwner = request.OwnerId,
                LeaseExpiresAt = request.LeaseExpiresAt,
                LeaseGeneration = request.LeaseGeneration
            });
    }

    private async ValueTask<WorkflowStepRun> InsertSupersedingRevisionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        WorkflowStepRun existing,
        StepClaimRequest request,
        long leaseGeneration,
        CancellationToken cancellationToken)
    {
        var superseding = new WorkflowStepRun
        {
            Id = Guid.NewGuid(),
            WorkflowRunId = existing.WorkflowRunId,
            StepKey = existing.StepKey,
            ImplementationKey = request.ImplementationKey,
            Status = StepStatus.Pending,
            Attempt = 0,
            CreatedAt = request.Now,
            InputJson = request.InputJson,
            InputType = request.InputType,
            InputHash = request.InputHash,
            OutputType = request.OutputType,
            Revision = existing.Revision + 1,
            LeaseGeneration = leaseGeneration
        };
        await insertStep.ExecuteAsync(connection, transaction, superseding, cancellationToken)
            .ConfigureAwait(false);
        return superseding;
    }

    private static WorkflowStepRun CancelledPlaceholder(StepClaimRequest request) =>
        UnscheduledPlaceholder(request, StepStatus.Cancelled);

    private static WorkflowStepRun UnscheduledPlaceholder(
        StepClaimRequest request, StepStatus status) =>
        new()
        {
            Id = Guid.Empty,
            WorkflowRunId = request.WorkflowRunId,
            StepKey = request.StepKey,
            ImplementationKey = request.ImplementationKey,
            Status = status,
            Attempt = 0,
            CreatedAt = request.Now,
            OutputType = request.OutputType
        };
}
