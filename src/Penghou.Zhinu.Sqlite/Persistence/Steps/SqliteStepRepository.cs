using Microsoft.Data.Sqlite;
using System.Text.Json;
using Penghou.Zhinu.Sqlite.Persistence.Leases;
using Penghou.Zhinu.Sqlite.Persistence.Workflows;

namespace Penghou.Zhinu.Sqlite.Persistence.Steps;

/// <summary>
/// Coordinates step claims, completions, failures, restarts, rollbacks,
/// compensations, and durable operations.
/// </summary>
internal sealed partial class SqliteStepRepository :
    IWorkflowStepRepository,
    IWorkflowForkRepository,
    IIdempotentWorkflowRestartRepository
{
    private const string StepRestartOperationType = "step-restart";
    private readonly IZhinuSqliteDatabase factory;
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
    private readonly GetBoundGenerationStatusQuery getBoundGenerationStatus = new();
    private readonly GetStepQuery getStep = new();
    private readonly GetStepByIdQuery getStepById = new();
    private readonly GetCurrentStepsQuery getCurrentSteps = new();
    private readonly GetStepDependenciesQuery getStepDependencies = new();
    private readonly GetCompensationsQuery getCompensations = new();
    private readonly GetActiveOperationQuery getActiveOperation = new();
    private readonly InsertEventCommand insertEvent = new();
    private readonly GetRunQuery getRun = new();
    private readonly InsertRunCommand insertRun = new();

    public SqliteStepRepository(IZhinuSqliteDatabase factory)
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

    public ValueTask<IReadOnlyList<WorkflowEvent>> CompleteStepWithEventsAsync(
        Guid stepId,
        string ownerId,
        string? outputJson,
        DateTimeOffset now,
        IReadOnlyList<PendingWorkflowEvent>? events,
        CancellationToken cancellationToken = default) =>
        stepFinisher.FinishStepAsync(
            stepId,
            ownerId,
            StepStatus.Completed,
            outputJson,
            null,
            null,
            WorkflowEventTypes.StepCompleted,
            now,
            cancellationToken,
            events);

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
            throw new WorkflowNotFoundException($"Step '{stepId:D}' does not exist.");
        var status = retryAt is null ? StepStatus.Failed : StepStatus.Waiting;
        StepStateMachine.AssertCanTransition(step.Status, status, stepId);
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
        var additions = dependsOn.Distinct(StringComparer.Ordinal).ToArray();
        if (additions.Contains(stepKey, StringComparer.Ordinal))
            throw new WorkflowStateException($"Step '{stepKey}' cannot depend on itself.");
        var existing = await getStepDependencies.ExecuteAsync(
            connection, transaction, workflowRunId, cancellationToken).ConfigureAwait(false);
        var combined = new List<StepDependency>(existing);
        combined.AddRange(additions.Select(dependency => new StepDependency(stepKey, dependency)));
        if (WorkflowDependencyValidator.HasCycle(combined))
            throw new WorkflowStateException($"Adding dependencies for step '{stepKey}' would create a cycle.");
        foreach (var dependency in additions)
        {
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
        if (request.AuthorizationDeclarationHash is { } authorizationHash &&
            (authorizationHash.Length != 64 || authorizationHash.Any(character => !Uri.IsHexDigit(character))))
            throw new ArgumentException("Authorization declaration hash must be a SHA-256 hex digest.", nameof(request));
    }

    private static void ValidateStepContract(
        WorkflowStepRun existing,
        StepClaimRequest request)
    {
        if (!ContractMatches(existing, request))
        {
            throw new WorkflowStateException(
                $"Step key '{request.StepKey}' was reused with an incompatible input or result contract, " +
                "or with a different implementation key.");
        }
    }

    private static bool ContractMatches(
        WorkflowStepRun existing,
        StepClaimRequest request)
    {
        // A Pending revision was created by a restart or fork and has not
        // committed anything: its input value is re-derived on execution (for
        // example a child:wait step whose input is the child id produced by a
        // restarted child:start). Only the type contract must match; the value
        // hash is established by the re-execution. Committed or in-flight steps
        // keep the durable-reuse contract on the value hash.
        var valueHashMatches = existing.Status == StepStatus.Pending ||
            string.Equals(existing.InputHash, request.InputHash, StringComparison.Ordinal);
        return string.Equals(existing.InputType, request.InputType, StringComparison.Ordinal) &&
            valueHashMatches &&
            string.Equals(existing.OutputType, request.OutputType, StringComparison.Ordinal) &&
            string.Equals(
                existing.ImplementationKey,
                request.ImplementationKey,
                StringComparison.Ordinal) &&
            string.Equals(existing.AuthorizationDeclarationHash,
                request.AuthorizationDeclarationHash, StringComparison.Ordinal);
    }
}
