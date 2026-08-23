using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Text.Json;
using Penghou.Zhinu.Execution.Outcomes;

namespace Penghou.Zhinu.Execution.Steps;

/// <summary>
/// Advances one run through its forward execution: claims the run lease,
/// reconstructs the context, executes the registered workflow, and settles
/// the run to completed, failed, or cancelled.
/// </summary>
internal sealed class RunExecutionPipeline
{
    private readonly IWorkflowStore store;
    private readonly IWorkflowRegistry registry;
    private readonly ZhinuOptions options;
    private readonly JsonSerializerOptions serializerOptions;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<WorkflowEngine> logger;
    private readonly IWorkflowEventPublisher? eventPublisher;
    private readonly string ownerId;
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> runningCancellations;
    private readonly Action<Guid> notifyEventAppended;
    private readonly Func<Guid, CancellationToken, Task> resumeRollbackRestart;
    private readonly RunOutcomeHandler outcomeHandler;

    public RunExecutionPipeline(
        IWorkflowStore store,
        IWorkflowRegistry registry,
        ZhinuOptions options,
        JsonSerializerOptions serializerOptions,
        TimeProvider timeProvider,
        ILogger<WorkflowEngine> logger,
        IWorkflowEventPublisher? eventPublisher,
        string ownerId,
        ConcurrentDictionary<Guid, CancellationTokenSource> runningCancellations,
        Action<Guid> notifyEventAppended,
        Func<Guid, CancellationToken, Task> resumeRollbackRestart,
        RunOutcomeHandler outcomeHandler)
    {
        this.store = store;
        this.registry = registry;
        this.options = options;
        this.serializerOptions = serializerOptions;
        this.timeProvider = timeProvider;
        this.logger = logger;
        this.eventPublisher = eventPublisher;
        this.ownerId = ownerId;
        this.runningCancellations = runningCancellations;
        this.notifyEventAppended = notifyEventAppended;
        this.resumeRollbackRestart = resumeRollbackRestart;
        this.outcomeHandler = outcomeHandler;
    }

    public async Task ExecuteAsync(
        Guid workflowRunId,
        CancellationToken cancellationToken,
        int depth)
    {
        if (depth > options.MaxNestingDepth)
            return;
        var run = await store.GetRunAsync(workflowRunId, cancellationToken)
            .ConfigureAwait(false) ??
            throw new WorkflowNotFoundException(
                $"Workflow '{workflowRunId:D}' does not exist.");
        if (IsTerminal(run.Status))
            return;
        if (run.Status == WorkflowStatus.RollingBack)
        {
            await resumeRollbackRestart(workflowRunId, cancellationToken)
                .ConfigureAwait(false);
            return;
        }
        var now = timeProvider.GetUtcNow();
        var claimStarted = timeProvider.GetTimestamp();
        var leaseGeneration = await store.TryClaimRunAsync(
                workflowRunId,
                ownerId,
                now,
                now + options.LeaseDuration,
                cancellationToken).ConfigureAwait(false);
        ZhinuDiagnostics.ClaimLatencyHistogram.Record(
            timeProvider.GetElapsedTime(claimStarted).TotalSeconds);
        if (leaseGeneration is null)
        {
            return;
        }
        if (run.Deadline is { } deadline && now > deadline)
        {
            await store.FailRunAsync(
                workflowRunId,
                ownerId,
                WorkflowError.FromException(
                    new TimeoutException(
                        $"Workflow '{workflowRunId:D}' exceeded its deadline of {deadline:O}."),
                    now),
                now,
                CancellationToken.None).ConfigureAwait(false);
            notifyEventAppended(workflowRunId);
            return;
        }

        using var runCancellation = CancellationTokenSource
            .CreateLinkedTokenSource(cancellationToken);
        if (!runningCancellations.TryAdd(workflowRunId, runCancellation))
        {
            await store.ReleaseRunLeaseAsync(
                workflowRunId,
                ownerId,
                timeProvider.GetUtcNow(),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        await using var renewal = new LeaseRenewal(
            timeProvider,
            options.LeaseRenewalInterval,
            token => store.RenewRunLeaseAsync(
                workflowRunId,
                ownerId,
                timeProvider.GetUtcNow() + options.LeaseDuration,
                token));
        try
        {
            if (!registry.TryGet(
                    run.WorkflowName,
                    run.WorkflowVersion,
                    out var registration))
            {
                throw new WorkflowDefinitionUnavailableException(
                    run.WorkflowName,
                    run.WorkflowVersion);
            }
            ValidateRunTypes(run, registration!);
            var context = new WorkflowContext(
                workflowRunId,
                store,
                ownerId,
                options,
                serializerOptions,
                timeProvider,
                leaseGeneration.Value,
                runCancellation.Token,
                eventPublisher,
                executeChildRun: (childId, childCancellation) =>
                    ExecuteAsync(
                        childId,
                        childCancellation,
                        depth + 1),
                onEventAppended: notifyEventAppended);
            logger.LogInformation(
                ZhinuLogEvents.RunExecuting,
                "Executing workflow {WorkflowRunId} ({WorkflowName} {WorkflowVersion}).",
                workflowRunId,
                run.WorkflowName,
                run.WorkflowVersion);
            var outputJson = await registration!.ExecuteAsync(
                context,
                run.InputJson ?? "null",
                serializerOptions,
                runCancellation.Token).ConfigureAwait(false);
            await outcomeHandler.CompleteAsync(
                workflowRunId,
                outputJson,
                SerializationIdentity.TypeId(registration.OutputType)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (runCancellation.IsCancellationRequested)
        {
            await outcomeHandler.ReleaseLeaseOnCancellationAsync(workflowRunId)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await outcomeHandler.FailOnExceptionAsync(workflowRunId, exception)
                .ConfigureAwait(false);
        }
        finally
        {
            runningCancellations.TryRemove(workflowRunId, out _);
        }
    }

    internal static bool IsTerminal(WorkflowStatus status) =>
        status is WorkflowStatus.Completed or WorkflowStatus.Failed or
            WorkflowStatus.Cancelled or WorkflowStatus.Compensated;

    private static void ValidateRunTypes(
        WorkflowRun run,
        IWorkflowRegistration registration)
    {
        var inputType = SerializationIdentity.TypeId(registration.InputType);
        var outputType = SerializationIdentity.TypeId(registration.OutputType);
        if (!string.Equals(run.InputType, inputType, StringComparison.Ordinal) ||
            !string.Equals(run.OutputType, outputType, StringComparison.Ordinal))
        {
            throw new WorkflowSerializationException(
                $"Stored workflow type contract does not match registered workflow '{run.WorkflowName}' version '{run.WorkflowVersion}'.");
        }
        // A declarative run records the fingerprint of the definition it was
        // started from. A different registered definition for the same name and
        // version must not silently replay the older run's durable state.
        if (run.DefinitionFingerprint is not null &&
            registration.DefinitionFingerprint is not null &&
            !string.Equals(
                run.DefinitionFingerprint,
                registration.DefinitionFingerprint,
                StringComparison.Ordinal))
        {
            throw new WorkflowSerializationException(
                $"Registered definition for workflow '{run.WorkflowName}' version '{run.WorkflowVersion}' " +
                $"fingerprint does not match the run's recorded fingerprint '{run.DefinitionFingerprint}'.");
        }
    }
}
