using Microsoft.Data.Sqlite;
using System.Text.Json;
using Penghou.Zhinu.Sqlite.Persistence.Leases;
using Penghou.Zhinu.Sqlite.Persistence.Workflows;

namespace Penghou.Zhinu.Sqlite.Persistence.Steps;

/// <summary>
/// Coordinates step claims, completions, failures, restarts, rollbacks,
/// compensations, and durable operations.
/// </summary>
internal sealed class SqliteStepRepository : IWorkflowStepRepository
{
    private readonly SqliteConnectionFactory factory;
    private readonly SqliteStepFinisher stepFinisher;
    private readonly InsertStepCommand insertStep = new();
    private readonly ClaimStepCommand claimStep = new();
    private readonly FailStepCommand failStep = new();
    private readonly SkipCompensationCommand skipCompensation = new();
    private readonly InsertCompensationCommand insertCompensation = new();
    private readonly InsertStepDependencyCommand insertStepDependency = new();
    private readonly RenewStepLeaseCommand renewStepLease = new();
    private readonly BumpRunGenerationCommand bumpRunGeneration = new();
    private readonly ResetRunForRestartCommand resetRunForRestart = new();
    private readonly InsertOperationCommand insertOperation = new();
    private readonly UpdateOperationStatusCommand updateOperationStatus = new();
    private readonly CompleteOperationCommand completeOperation = new();
    private readonly FailOperationCommand failOperation = new();
    private readonly ClaimRollbackCommand claimRollback = new();
    private readonly RenewRollbackLeaseCommand renewRollbackLease = new();
    private readonly ReleaseRollbackLeaseCommand releaseRollbackLease = new();
    private readonly CompleteRollbackCommand completeRollback = new();
    private readonly FailRollbackCommand failRollback = new();
    private readonly ClaimRollbackAndRestartCommand claimRollbackAndRestart = new();
    private readonly RenewRollbackAndRestartLeaseCommand renewRollbackAndRestartLease = new();
    private readonly ReleaseRollbackAndRestartLeaseCommand releaseRollbackAndRestartLease = new();
    private readonly ResetRunForRollbackAndRestartCommand resetRunForRollbackAndRestart = new();
    private readonly FailRollbackAndRestartCommand failRollbackAndRestart = new();
    private readonly ClaimCompensationCommand claimCompensation = new();
    private readonly CompleteCompensationCommand completeCompensation = new();
    private readonly FailCompensationCommand failCompensation = new();
    private readonly GetRunStatusQuery getRunStatus = new();
    private readonly GetRunLeaseGenerationQuery getRunLeaseGeneration = new();
    private readonly GetStepQuery getStep = new();
    private readonly GetStepByIdQuery getStepById = new();
    private readonly GetCurrentStepsQuery getCurrentSteps = new();
    private readonly GetStepDependenciesQuery getStepDependencies = new();
    private readonly GetCompensationsQuery getCompensations = new();
    private readonly GetActiveOperationQuery getActiveOperation = new();
    private readonly InsertEventCommand insertEvent = new();

    public SqliteStepRepository(SqliteConnectionFactory factory)
    {
        this.factory = factory;
        stepFinisher = new(factory);
    }

    public async ValueTask<IReadOnlyList<WorkflowStepRun>> GetStepsAsync(
        Guid workflowRunId,
        CancellationToken cancellationToken = default)
    {
        await factory.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await factory.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        return await getCurrentSteps.ExecuteAsync(
            connection,
            null,
            workflowRunId,
            cancellationToken).ConfigureAwait(false);
    }

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
            throw new LeaseLostException(
                $"Workflow '{request.WorkflowRunId:D}' lease generation {request.LeaseGeneration} " +
                $"no longer matches the current generation {runGeneration}.");
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

    public async ValueTask<bool> RenewStepLeaseAsync(
        Guid stepId,
        string ownerId,
        DateTimeOffset leaseExpiresAt,
        CancellationToken cancellationToken = default)
    {
        await factory.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await factory.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        return await renewStepLease.ExecuteAsync(
            connection,
            stepId,
            ownerId,
            leaseExpiresAt,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask CompleteStepAsync(
        Guid stepId,
        string ownerId,
        string? outputJson,
        DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        await stepFinisher.FinishStepAsync(
            stepId,
            ownerId,
            StepStatus.Completed,
            outputJson,
            null,
            null,
            WorkflowEventTypes.StepCompleted,
            now,
            cancellationToken).ConfigureAwait(false);

    public async ValueTask FailStepAsync(
        Guid stepId,
        string ownerId,
        WorkflowError error,
        DateTimeOffset? retryAt,
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
        var status = retryAt is null ? StepStatus.Failed : StepStatus.Waiting;
        if (await failStep.ExecuteAsync(
            connection,
            transaction,
            stepId,
            ownerId,
            status,
            error,
            retryAt,
            now,
            cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new WorkflowStateException("Step failure requires an owned running lease.");
        }
        if (retryAt is null)
        {
            await skipCompensation.ExecuteAsync(
                connection,
                transaction,
                step.WorkflowRunId,
                step.StepKey,
                step.Revision,
                cancellationToken).ConfigureAwait(false);
        }
        await insertEvent.ExecuteAsync(
            connection,
            transaction,
            step.WorkflowRunId,
            step.StepKey,
            WorkflowEventTypes.StepFailed,
            now,
            step.Attempt,
            JsonSerializer.Serialize(error, SqliteStoreSupport.SerializerOptions),
            cancellationToken).ConfigureAwait(false);
        if (retryAt is not null)
        {
            await insertEvent.ExecuteAsync(
                connection,
                transaction,
                step.WorkflowRunId,
                step.StepKey,
                WorkflowEventTypes.RetryScheduled,
                now,
                step.Attempt,
                JsonSerializer.Serialize(
                    new { availableAt = retryAt },
                    SqliteStoreSupport.SerializerOptions),
                cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<StepDependency>> GetStepDependenciesAsync(
        Guid workflowRunId,
        CancellationToken cancellationToken = default)
    {
        if (workflowRunId == Guid.Empty)
            throw new ArgumentException("Workflow ID must not be empty.", nameof(workflowRunId));
        await factory.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await factory.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        return await getStepDependencies.ExecuteAsync(
            connection,
            null,
            workflowRunId,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<WorkflowStepCompensation>> GetCompensationsAsync(
        Guid workflowRunId,
        CancellationToken cancellationToken = default)
    {
        await factory.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await factory.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        return await getCompensations.ExecuteAsync(
            connection,
            null,
            workflowRunId,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<RestartPlan> PlanRestartAsync(
        Guid workflowRunId,
        string stepKey,
        StepRestartMode mode,
        CancellationToken cancellationToken = default)
    {
        if (workflowRunId == Guid.Empty)
            throw new ArgumentException("Workflow ID must not be empty.", nameof(workflowRunId));
        ArgumentException.ThrowIfNullOrWhiteSpace(stepKey);
        await factory.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await factory.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var plan = await ResolveRestartPlanAsync(
            connection,
            transaction,
            workflowRunId,
            stepKey,
            mode,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return plan;
    }

    public async ValueTask<RestartPlan> RestartStepAsync(
        Guid workflowRunId,
        string stepKey,
        StepRestartMode mode,
        string? actor,
        string? reason,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        if (workflowRunId == Guid.Empty)
            throw new ArgumentException("Workflow ID must not be empty.", nameof(workflowRunId));
        ArgumentException.ThrowIfNullOrWhiteSpace(stepKey);
        await factory.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await factory.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var plan = await ResolveRestartPlanAsync(
            connection,
            transaction,
            workflowRunId,
            stepKey,
            mode,
            cancellationToken).ConfigureAwait(false);
        await bumpRunGeneration.ExecuteAsync(
            connection,
            transaction,
            workflowRunId,
            cancellationToken).ConfigureAwait(false);
        var generation = await getRunLeaseGeneration.ExecuteAsync(
            connection,
            transaction,
            workflowRunId,
            cancellationToken).ConfigureAwait(false);
        await resetRunForRestart.ExecuteAsync(
            connection,
            transaction,
            workflowRunId,
            now,
            cancellationToken).ConfigureAwait(false);
        foreach (var entry in plan.StepsToInvalidate)
        {
            var latest = await getStep.ExecuteAsync(
                connection,
                transaction,
                workflowRunId,
                entry.StepKey,
                cancellationToken).ConfigureAwait(false) ??
                throw new KeyNotFoundException(
                    $"Step '{entry.StepKey}' does not exist in workflow '{workflowRunId:D}'.");
            var next = new WorkflowStepRun
            {
                Id = Guid.NewGuid(),
                WorkflowRunId = workflowRunId,
                StepKey = entry.StepKey,
                Status = StepStatus.Pending,
                Attempt = 0,
                CreatedAt = now,
                InputJson = latest.InputJson,
                InputType = latest.InputType,
                InputHash = latest.InputHash,
                OutputType = latest.OutputType,
                SignalName = latest.SignalName,
                Revision = latest.Revision + 1,
                LeaseGeneration = generation
            };
            await insertStep.ExecuteAsync(connection, transaction, next, cancellationToken)
                .ConfigureAwait(false);
        }
        await insertEvent.ExecuteAsync(
            connection,
            transaction,
            workflowRunId,
            stepKey,
            WorkflowEventTypes.StepRestarted,
            now,
            null,
            JsonSerializer.Serialize(
                new
                {
                    stepKey,
                    mode = mode.ToString(),
                    actor,
                    reason,
                    leaseGeneration = generation,
                    invalidatedSteps = plan.StepsToInvalidate
                        .Select(item => new
                        {
                            stepKey = item.StepKey,
                            reason = item.Reason.ToString()
                        })
                        .ToArray()
                },
                SqliteStoreSupport.SerializerOptions),
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return plan;
    }

    public async ValueTask<RollbackPlan> PlanRollbackAsync(
        Guid workflowRunId,
        string? targetStepKey,
        RollbackBoundary boundary,
        CancellationToken cancellationToken = default)
    {
        if (workflowRunId == Guid.Empty)
            throw new ArgumentException("Workflow ID must not be empty.", nameof(workflowRunId));
        if (targetStepKey is not null)
            ArgumentException.ThrowIfNullOrWhiteSpace(targetStepKey);
        await factory.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await factory.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var steps = await getCurrentSteps.ExecuteAsync(
            connection,
            transaction,
            workflowRunId,
            cancellationToken).ConfigureAwait(false);
        var dependencies = await getStepDependencies.ExecuteAsync(
            connection,
            transaction,
            workflowRunId,
            cancellationToken).ConfigureAwait(false);
        var compensations = await getCompensations.ExecuteAsync(
            connection,
            transaction,
            workflowRunId,
            cancellationToken).ConfigureAwait(false);
        var plan = ResolveRollbackPlan(
            workflowRunId,
            targetStepKey,
            boundary,
            steps,
            dependencies,
            compensations);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return plan;
    }

    public async ValueTask<long?> ClaimRollbackAsync(
        Guid workflowRunId,
        string ownerId,
        DateTimeOffset now,
        DateTimeOffset leaseExpiresAt,
        CancellationToken cancellationToken = default)
    {
        await factory.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await factory.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var generation = await claimRollback.ExecuteAsync(
            connection,
            transaction,
            workflowRunId,
            ownerId,
            now,
            leaseExpiresAt,
            cancellationToken).ConfigureAwait(false);
        if (generation is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return generation;
    }

    public async ValueTask<bool> RenewRollbackLeaseAsync(
        Guid workflowRunId,
        string ownerId,
        DateTimeOffset leaseExpiresAt,
        CancellationToken cancellationToken = default)
    {
        await factory.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await factory.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        return await renewRollbackLease.ExecuteAsync(
            connection,
            workflowRunId,
            ownerId,
            leaseExpiresAt,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask ReleaseRollbackLeaseAsync(
        Guid workflowRunId,
        string ownerId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await factory.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await factory.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        await releaseRollbackLease.ExecuteAsync(
            connection,
            workflowRunId,
            ownerId,
            now,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<bool> CompleteRollbackAsync(
        Guid workflowRunId,
        string ownerId,
        long generation,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await factory.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await factory.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        if (await completeRollback.ExecuteAsync(
            connection,
            transaction,
            workflowRunId,
            ownerId,
            generation,
            now,
            cancellationToken).ConfigureAwait(false) != 1)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async ValueTask FailRollbackAsync(
        Guid workflowRunId,
        string ownerId,
        long generation,
        WorkflowError error,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await factory.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await factory.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        if (await failRollback.ExecuteAsync(
            connection,
            transaction,
            workflowRunId,
            ownerId,
            generation,
            error,
            now,
            cancellationToken).ConfigureAwait(false) != 1)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return;
        }
        await insertEvent.ExecuteAsync(
            connection,
            transaction,
            workflowRunId,
            null,
            WorkflowEventTypes.WorkflowFailed,
            now,
            null,
            JsonSerializer.Serialize(error, SqliteStoreSupport.SerializerOptions),
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<WorkflowStepCompensation?> ClaimCompensationAsync(
        Guid workflowRunId,
        string stepKey,
        string ownerId,
        long generation,
        DateTimeOffset now,
        DateTimeOffset leaseExpiresAt,
        string? actor,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        await factory.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await factory.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var claimed = await claimCompensation.ExecuteAsync(
            connection,
            transaction,
            workflowRunId,
            stepKey,
            ownerId,
            generation,
            now,
            leaseExpiresAt,
            actor,
            reason,
            cancellationToken).ConfigureAwait(false);
        if (claimed is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return claimed;
    }

    public async ValueTask CompleteCompensationAsync(
        Guid compensationId,
        string ownerId,
        string? outputJson,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await factory.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await factory.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        if (await completeCompensation.ExecuteAsync(
            connection,
            transaction,
            compensationId,
            ownerId,
            outputJson,
            now,
            cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new WorkflowStateException(
                "Compensation completion requires an owned running lease.");
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask FailCompensationAsync(
        Guid compensationId,
        string ownerId,
        WorkflowError error,
        DateTimeOffset? retryAt,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await factory.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await factory.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        if (await failCompensation.ExecuteAsync(
            connection,
            transaction,
            compensationId,
            ownerId,
            error,
            retryAt,
            now,
            cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new WorkflowStateException(
                "Compensation failure requires an owned running lease.");
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask CreateOperationAsync(
        WorkflowRunOperation operation,
        CancellationToken cancellationToken = default)
    {
        await factory.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await factory.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        await insertOperation.ExecuteAsync(connection, operation, cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<WorkflowRunOperation?> GetActiveOperationAsync(
        Guid workflowRunId,
        CancellationToken cancellationToken = default)
    {
        await factory.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await factory.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        return await getActiveOperation.ExecuteAsync(
            connection,
            workflowRunId,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<bool> UpdateOperationStatusAsync(
        Guid operationId,
        WorkflowOperationStatus status,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await factory.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await factory.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        return await updateOperationStatus.ExecuteAsync(
            connection,
            operationId,
            status,
            now,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<long?> ClaimRollbackAndRestartAsync(
        Guid workflowRunId,
        string ownerId,
        DateTimeOffset now,
        DateTimeOffset leaseExpiresAt,
        CancellationToken cancellationToken = default)
    {
        await factory.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await factory.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var generation = await claimRollbackAndRestart.ExecuteAsync(
            connection,
            transaction,
            workflowRunId,
            ownerId,
            now,
            leaseExpiresAt,
            cancellationToken).ConfigureAwait(false);
        if (generation is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return generation;
    }

    public async ValueTask<bool> RenewRollbackAndRestartLeaseAsync(
        Guid workflowRunId,
        string ownerId,
        DateTimeOffset leaseExpiresAt,
        CancellationToken cancellationToken = default)
    {
        await factory.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await factory.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        return await renewRollbackAndRestartLease.ExecuteAsync(
            connection,
            workflowRunId,
            ownerId,
            leaseExpiresAt,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask ReleaseRollbackAndRestartLeaseAsync(
        Guid workflowRunId,
        string ownerId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await factory.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await factory.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        await releaseRollbackAndRestartLease.ExecuteAsync(
            connection,
            workflowRunId,
            ownerId,
            now,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<bool> CompleteRollbackAndRestartAsync(
        Guid workflowRunId,
        string ownerId,
        long generation,
        Guid operationId,
        IReadOnlyList<string> invalidateStepKeys,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await factory.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await factory.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        if (await resetRunForRollbackAndRestart.ExecuteAsync(
            connection,
            transaction,
            workflowRunId,
            ownerId,
            generation,
            now,
            cancellationToken).ConfigureAwait(false) != 1)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }
        var newGeneration = await getRunLeaseGeneration.ExecuteAsync(
            connection,
            transaction,
            workflowRunId,
            cancellationToken).ConfigureAwait(false);
        foreach (var stepKey in invalidateStepKeys)
        {
            var latest = await getStep.ExecuteAsync(
                connection,
                transaction,
                workflowRunId,
                stepKey,
                cancellationToken).ConfigureAwait(false);
            if (latest is null)
                continue;
            var next = new WorkflowStepRun
            {
                Id = Guid.NewGuid(),
                WorkflowRunId = workflowRunId,
                StepKey = stepKey,
                Status = StepStatus.Pending,
                Attempt = 0,
                CreatedAt = now,
                InputJson = latest.InputJson,
                InputType = latest.InputType,
                InputHash = latest.InputHash,
                OutputType = latest.OutputType,
                SignalName = latest.SignalName,
                Revision = latest.Revision + 1,
                LeaseGeneration = newGeneration
            };
            await insertStep.ExecuteAsync(connection, transaction, next, cancellationToken)
                .ConfigureAwait(false);
        }
        await completeOperation.ExecuteAsync(
            connection,
            transaction,
            operationId,
            now,
            cancellationToken).ConfigureAwait(false);
        await insertEvent.ExecuteAsync(
            connection,
            transaction,
            workflowRunId,
            null,
            WorkflowEventTypes.WorkflowRestarted,
            now,
            null,
            JsonSerializer.Serialize(
                new
                {
                    invalidatedSteps = invalidateStepKeys.Count,
                    leaseGeneration = newGeneration
                },
                SqliteStoreSupport.SerializerOptions),
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async ValueTask FailRollbackAndRestartAsync(
        Guid workflowRunId,
        string ownerId,
        long generation,
        Guid operationId,
        WorkflowError error,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await factory.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await factory.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var released = await failRollbackAndRestart.ExecuteAsync(
            connection,
            transaction,
            workflowRunId,
            ownerId,
            generation,
            error,
            now,
            cancellationToken).ConfigureAwait(false);
        await failOperation.ExecuteAsync(
            connection,
            transaction,
            operationId,
            now,
            cancellationToken).ConfigureAwait(false);
        if (released != 1)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return;
        }
        await insertEvent.ExecuteAsync(
            connection,
            transaction,
            workflowRunId,
            null,
            WorkflowEventTypes.WorkflowFailed,
            now,
            null,
            JsonSerializer.Serialize(error, SqliteStoreSupport.SerializerOptions),
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<RestartPlan> ResolveRestartPlanAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid workflowRunId,
        string stepKey,
        StepRestartMode mode,
        CancellationToken cancellationToken)
    {
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
        var target = await getStep.ExecuteAsync(
            connection,
            transaction,
            workflowRunId,
            stepKey,
            cancellationToken).ConfigureAwait(false) ??
            throw new KeyNotFoundException(
                $"Step '{stepKey}' does not exist in workflow '{workflowRunId:D}'.");
        var steps = await getCurrentSteps.ExecuteAsync(
            connection,
            transaction,
            workflowRunId,
            cancellationToken).ConfigureAwait(false);
        var invalidated = mode switch
        {
            StepRestartMode.StepOnly =>
                new List<RestartPlanStep>
                {
                    new(stepKey, RestartReason.Requested)
                },
            StepRestartMode.CreationOrder =>
                ResolveCreationOrder(steps, target),
            StepRestartMode.Dependents =>
                await ResolveDependentsAsync(
                    connection,
                    transaction,
                    steps,
                    workflowRunId,
                    stepKey,
                    target,
                    cancellationToken).ConfigureAwait(false),
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };
        return new RestartPlan(workflowRunId, stepKey, invalidated);
    }

    private async ValueTask<List<RestartPlanStep>> ResolveDependentsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<WorkflowStepRun> steps,
        Guid workflowRunId,
        string stepKey,
        WorkflowStepRun target,
        CancellationToken cancellationToken)
    {
        var dependencies = await getStepDependencies.ExecuteAsync(
            connection,
            transaction,
            workflowRunId,
            cancellationToken).ConfigureAwait(false);
        var dependentsOf = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var dependency in dependencies)
        {
            if (!dependentsOf.TryGetValue(
                    dependency.DependsOnStepKey,
                    out var list))
            {
                list = dependentsOf[dependency.DependsOnStepKey] = [];
            }
            list.Add(dependency.StepKey);
        }
        var visited = new HashSet<string>(StringComparer.Ordinal) { stepKey };
        var queue = new Queue<string>();
        queue.Enqueue(stepKey);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!dependentsOf.TryGetValue(current, out var dependents))
                continue;
            foreach (var dependent in dependents)
            {
                if (visited.Add(dependent))
                    queue.Enqueue(dependent);
            }
        }
        var ordered = steps
            .Where(step => visited.Contains(step.StepKey) && step.StepKey != stepKey)
            .OrderBy(step => step.CreatedAt)
            .ThenBy(step => step.StepKey, StringComparer.Ordinal)
            .Select(step => new RestartPlanStep(
                step.StepKey,
                RestartReason.Dependent))
            .ToList();
        ordered.Insert(0, new RestartPlanStep(stepKey, RestartReason.Requested));
        return ordered;
    }

    private static List<RestartPlanStep> ResolveCreationOrder(
        IReadOnlyList<WorkflowStepRun> steps,
        WorkflowStepRun target)
    {
        var invalidated = steps
            .Where(step => step.CreatedAt >= target.CreatedAt &&
                           step.StepKey != target.StepKey)
            .OrderBy(step => step.CreatedAt)
            .ThenBy(step => step.StepKey, StringComparer.Ordinal)
            .Select(step => new RestartPlanStep(
                step.StepKey,
                RestartReason.CreationOrderFallback))
            .ToList();
        invalidated.Insert(0, new RestartPlanStep(target.StepKey, RestartReason.Requested));
        return invalidated;
    }

    private static RollbackPlan ResolveRollbackPlan(
        Guid workflowRunId,
        string? targetStepKey,
        RollbackBoundary boundary,
        IReadOnlyList<WorkflowStepRun> steps,
        IReadOnlyList<StepDependency> dependencies,
        IReadOnlyList<WorkflowStepCompensation> compensations)
    {
        var stepKeys = steps
            .Select(step => step.StepKey)
            .ToHashSet(StringComparer.Ordinal);
        var claimableByKey = new Dictionary<string, WorkflowStepCompensation>(
            StringComparer.Ordinal);
        foreach (var row in compensations.OrderBy(item => item.Revision))
        {
            if (stepKeys.Contains(row.StepKey) &&
                row.InputJson is not null &&
                row.Status is CompensationStatus.Pending or CompensationStatus.Failed)
            {
                claimableByKey[row.StepKey] = row;
            }
        }

        bool Compensable(string stepKey) =>
            claimableByKey.ContainsKey(stepKey);

        var topoOrder = TopologicalOrder(steps, dependencies);
        var topoIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < topoOrder.Count; index++)
            topoIndex[topoOrder[index].StepKey] = index;

        var compensated = new List<RollbackPlanStep>();
        var preserved = new List<RollbackPlanStep>();

        if (targetStepKey is null)
        {
            foreach (var step in topoOrder)
            {
                if (Compensable(step.StepKey))
                {
                    compensated.Add(new RollbackPlanStep(
                        step.StepKey,
                        RollbackAction.Compensate,
                        RollbackReason.Dependent));
                }
            }
            foreach (var step in steps
                .OrderBy(item => item.CreatedAt)
                .ThenBy(item => item.StepKey, StringComparer.Ordinal))
            {
                if (!Compensable(step.StepKey))
                {
                    preserved.Add(new RollbackPlanStep(
                        step.StepKey,
                        RollbackAction.Preserve,
                        RollbackReason.IndependentBranch));
                }
            }
            compensated.Sort((a, b) =>
                topoIndex[b.StepKey].CompareTo(topoIndex[a.StepKey]));
            return new RollbackPlan(
                workflowRunId,
                null,
                boundary,
                compensated.Concat(preserved).ToArray());
        }

        var dependents = new HashSet<string>(StringComparer.Ordinal);
        var ancestors = new HashSet<string>(StringComparer.Ordinal);
        if (Compensable(targetStepKey) ||
            steps.Any(step => step.StepKey == targetStepKey))
        {
            var dependentsOf = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var dependsOn = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var dependency in dependencies)
            {
                if (!stepKeys.Contains(dependency.StepKey) ||
                    !stepKeys.Contains(dependency.DependsOnStepKey))
                {
                    continue;
                }
                if (!dependentsOf.TryGetValue(
                        dependency.DependsOnStepKey,
                        out var dependentsList))
                {
                    dependentsList = dependentsOf[dependency.DependsOnStepKey] = [];
                }
                dependentsList.Add(dependency.StepKey);
                if (!dependsOn.TryGetValue(dependency.StepKey, out var ancestorsList))
                {
                    ancestorsList = dependsOn[dependency.StepKey] = [];
                }
                ancestorsList.Add(dependency.DependsOnStepKey);
            }
            dependents = TransitiveClosure(dependentsOf, targetStepKey);
            ancestors = TransitiveClosure(dependsOn, targetStepKey);
        }

        foreach (var step in steps
            .OrderBy(item => item.CreatedAt)
            .ThenBy(item => item.StepKey, StringComparer.Ordinal))
        {
            if (string.Equals(step.StepKey, targetStepKey, StringComparison.Ordinal))
            {
                if (boundary == RollbackBoundary.BeforeStep)
                {
                    compensated.Add(new RollbackPlanStep(
                        step.StepKey,
                        RollbackAction.Compensate,
                        RollbackReason.Boundary));
                }
                else
                {
                    preserved.Add(new RollbackPlanStep(
                        step.StepKey,
                        RollbackAction.Preserve,
                        RollbackReason.Boundary));
                }
            }
            else if (dependents.Contains(step.StepKey))
            {
                if (Compensable(step.StepKey))
                {
                    compensated.Add(new RollbackPlanStep(
                        step.StepKey,
                        RollbackAction.Compensate,
                        RollbackReason.Dependent));
                }
                else
                {
                    preserved.Add(new RollbackPlanStep(
                        step.StepKey,
                        RollbackAction.Preserve,
                        RollbackReason.Dependent));
                }
            }
            else if (ancestors.Contains(step.StepKey))
            {
                preserved.Add(new RollbackPlanStep(
                    step.StepKey,
                    RollbackAction.Preserve,
                    RollbackReason.Ancestor));
            }
            else
            {
                preserved.Add(new RollbackPlanStep(
                    step.StepKey,
                    RollbackAction.Preserve,
                    RollbackReason.IndependentBranch));
            }
        }

        compensated.Sort((a, b) =>
            topoIndex[b.StepKey].CompareTo(topoIndex[a.StepKey]));
        return new RollbackPlan(
            workflowRunId,
            targetStepKey,
            boundary,
            compensated.Concat(preserved).ToArray());
    }

    private static HashSet<string> TransitiveClosure(
        Dictionary<string, List<string>> adjacency,
        string start)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        queue.Enqueue(start);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!adjacency.TryGetValue(current, out var neighbors))
                continue;
            foreach (var neighbor in neighbors)
            {
                if (visited.Add(neighbor))
                    queue.Enqueue(neighbor);
            }
        }
        visited.Remove(start);
        return visited;
    }

    private static List<WorkflowStepRun> TopologicalOrder(
        IReadOnlyList<WorkflowStepRun> steps,
        IReadOnlyList<StepDependency> dependencies)
    {
        var byKey = steps.ToDictionary(
            step => step.StepKey,
            StringComparer.Ordinal);
        var dependsOn = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var dependency in dependencies)
        {
            if (!byKey.ContainsKey(dependency.StepKey) ||
                !byKey.ContainsKey(dependency.DependsOnStepKey))
            {
                continue;
            }
            if (!dependsOn.TryGetValue(dependency.StepKey, out var list))
            {
                list = dependsOn[dependency.StepKey] = [];
            }
            list.Add(dependency.DependsOnStepKey);
        }
        var ordered = new List<WorkflowStepRun>(steps.Count);
        var resolved = new HashSet<string>(StringComparer.Ordinal);
        var remaining = steps
            .OrderBy(step => step.CreatedAt)
            .ThenBy(step => step.StepKey, StringComparer.Ordinal)
            .ToList();
        while (remaining.Count > 0)
        {
            var next = remaining.FirstOrDefault(step =>
                !dependsOn.TryGetValue(step.StepKey, out var deps) ||
                deps.All(dependency =>
                    !byKey.ContainsKey(dependency) ||
                    resolved.Contains(dependency)));
            if (next is null)
            {
                ordered.AddRange(remaining);
                break;
            }
            ordered.Add(next);
            resolved.Add(next.StepKey);
            remaining.Remove(next);
        }
        return ordered;
    }

    private async ValueTask InsertDependenciesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid workflowRunId,
        string stepKey,
        IReadOnlyCollection<string>? dependsOn,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (dependsOn is null || dependsOn.Count == 0)
            return;
        foreach (var dependency in dependsOn.Distinct(StringComparer.Ordinal))
        {
            if (string.Equals(dependency, stepKey, StringComparison.Ordinal))
                continue;
            await insertStepDependency.ExecuteAsync(
                connection,
                transaction,
                workflowRunId,
                stepKey,
                dependency,
                now,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask InsertCompensationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        StepClaimRequest request,
        int revision,
        long leaseGeneration,
        CancellationToken cancellationToken)
    {
        if (request.Compensation is null)
            return;
        await insertCompensation.ExecuteAsync(
            connection,
            transaction,
            request.WorkflowRunId,
            request.StepKey,
            request.Compensation,
            revision,
            leaseGeneration,
            request.Now,
            cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateClaim(StepClaimRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.WorkflowRunId == Guid.Empty)
            throw new ArgumentException("Workflow ID must not be empty.", nameof(request));
        ArgumentException.ThrowIfNullOrWhiteSpace(request.StepKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OutputType);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OwnerId);
        if (request.LeaseExpiresAt <= request.Now)
            throw new ArgumentException("Lease must expire in the future.", nameof(request));
        if (request.DependsOn is { Count: > 0 } &&
            request.DependsOn.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException(
                "Dependency step keys must not be blank.",
                nameof(request));
        }
        if (request.Compensation is not null &&
            string.IsNullOrWhiteSpace(request.Compensation.Name))
        {
            throw new ArgumentException(
                "Compensation name must not be blank.",
                nameof(request));
        }
    }

    private static void ValidateStepContract(
        WorkflowStepRun existing,
        StepClaimRequest request)
    {
        if (!string.Equals(existing.InputType, request.InputType, StringComparison.Ordinal) ||
            !string.Equals(existing.InputHash, request.InputHash, StringComparison.Ordinal) ||
            !string.Equals(existing.OutputType, request.OutputType, StringComparison.Ordinal))
        {
            throw new WorkflowStateException(
                $"Step key '{request.StepKey}' was reused with an incompatible input or result contract.");
        }
    }

    private static WorkflowStepRun CancelledPlaceholder(StepClaimRequest request) =>
        new()
        {
            Id = Guid.Empty,
            WorkflowRunId = request.WorkflowRunId,
            StepKey = request.StepKey,
            Status = StepStatus.Cancelled,
            Attempt = 0,
            CreatedAt = request.Now,
            OutputType = request.OutputType
        };
}
