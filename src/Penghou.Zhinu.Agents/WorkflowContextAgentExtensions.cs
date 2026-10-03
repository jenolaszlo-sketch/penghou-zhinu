using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using System.Text.Json;
using AgentFrameworkWorkflow = Microsoft.Agents.AI.Workflows.Workflow;

namespace Penghou.Zhinu.Agents;

/// <summary>
/// Runs a Microsoft Agent Framework (MAF) graph workflow as a single durable
/// Zhinu step. The step commits the workflow's terminal output; on replay the
/// stored result is returned without re-running MAF. If a previous attempt of
/// the step crashed mid-run, execution resumes from the most recent checkpoint
/// in <see cref="ICheckpointStore{T}"/> instead of starting over.
/// </summary>
public static class WorkflowContextAgentExtensions
{
    private static readonly JsonSerializerOptions SerializerOptions =
        Penghou.Zhinu.ZhinuJsonDefaults.CreateDefault();

    /// <summary>
    /// Executes <paramref name="workflow"/> inside a durable Zhinu step named
    /// <paramref name="stepKey"/>. MAF checkpoints are written per superstep to
    /// <paramref name="checkpointStore"/> under a session derived from the run
    /// and step; a durable store such as <see cref="SqliteJsonCheckpointStore"/>
    /// lets the step survive a crash and continue from its last checkpoint.
    /// </summary>
    /// <typeparam name="TInput">The workflow input type. Must be non-nullable and serializable.</typeparam>
    /// <typeparam name="TOutput">The workflow's terminal output type. Must be serializable.</typeparam>
    /// <param name="context">The Zhinu workflow context.</param>
    /// <param name="stepKey">A stable durable step key, unique within the workflow run.</param>
    /// <param name="workflow">The MAF graph workflow to run.</param>
    /// <param name="input">The initial input message for the workflow.</param>
    /// <param name="checkpointStore">The checkpoint store backing the run. Return the most recent checkpoint first from <c>RetrieveIndexAsync</c> to enable resumption.</param>
    /// <param name="cancellationToken">Cancels the step and the underlying MAF run.</param>
    public static Task<TOutput> RunAgentWorkflowAsync<TInput, TOutput>(
        this WorkflowContext context,
        string stepKey,
        AgentFrameworkWorkflow workflow,
        TInput input,
        ICheckpointStore<JsonElement> checkpointStore,
        CancellationToken cancellationToken = default)
        where TInput : notnull
    {
        return RunAgentWorkflowAsync<TInput, TOutput>(
            context, stepKey, workflow, input, checkpointStore, null, cancellationToken);
    }

    /// <summary>
    /// Executes <paramref name="workflow"/> inside a durable Zhinu step with an
    /// explicit restart policy. <see cref="AgentRestartMode.Resume"/> (the
    /// default) resumes the latest checkpoint of the step session, including
    /// across step restarts. <see cref="AgentRestartMode.Fresh"/> starts a
    /// revision-scoped session instead, so a restarted step never reuses
    /// abandoned-session outputs; earlier checkpoints are retained, not deleted.
    /// A stream that ends without a compatible terminal result or a failure
    /// throws <see cref="AgentWorkflowExecutionException"/> instead of
    /// returning a default value.
    /// </summary>
    public static Task<TOutput> RunAgentWorkflowAsync<TInput, TOutput>(
        this WorkflowContext context,
        string stepKey,
        AgentFrameworkWorkflow workflow,
        TInput input,
        ICheckpointStore<JsonElement> checkpointStore,
        AgentStepOptions? options = null,
        CancellationToken cancellationToken = default)
        where TInput : notnull
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentNullException.ThrowIfNull(checkpointStore);
        ArgumentException.ThrowIfNullOrWhiteSpace(stepKey);
        var mode = options?.RestartMode ?? AgentRestartMode.Resume;
        if (!Enum.IsDefined(mode))
            throw new ArgumentOutOfRangeException(nameof(options));
        return context.StepAsync(
            stepKey,
            input,
            (value, step, ct) => ExecuteAsync<TInput, TOutput>(
                context.WorkflowRunId,
                stepKey,
                step.Revision,
                mode,
                workflow,
                value,
                checkpointStore,
                ct),
            null,
            cancellationToken);
    }

    private static async Task<TOutput> ExecuteAsync<TInput, TOutput>(
        Guid workflowRunId,
        string stepKey,
        int stepRevision,
        AgentRestartMode mode,
        AgentFrameworkWorkflow workflow,
        TInput input,
        ICheckpointStore<JsonElement> checkpointStore,
        CancellationToken cancellationToken)
        where TInput : notnull
    {
        var sessionId = mode == AgentRestartMode.Fresh
            ? $"{workflowRunId:D}:{stepKey}:rev{stepRevision}"
            : $"{workflowRunId:D}:{stepKey}";
        var checkpointManager = CheckpointManager.CreateJson(checkpointStore);
        var checkpoints = (await checkpointStore
            .RetrieveIndexAsync(sessionId).ConfigureAwait(false)).ToArray();
        await using var run = checkpoints.Length == 0
            ? await InProcessExecution.RunStreamingAsync(
                workflow,
                input,
                checkpointManager,
                sessionId,
                cancellationToken).ConfigureAwait(false)
            : await InProcessExecution.ResumeStreamingAsync(
                workflow,
                checkpoints[0],
                checkpointManager,
                cancellationToken).ConfigureAwait(false);

        TOutput? output = default;
        var hasOutput = false;
        Exception? failure = null;
        await foreach (var evt in run.WatchStreamAsync()
            .WithCancellation(cancellationToken))
        {
            switch (evt)
            {
                case WorkflowOutputEvent outputEvent:
                    var converted = ConvertOutput<TOutput>(outputEvent);
                    if (converted is not null)
                    {
                        output = converted;
                        hasOutput = true;
                    }
                    break;
                case WorkflowErrorEvent errorEvent:
                    failure = errorEvent.Exception;
                    break;
                case ExecutorFailedEvent failedEvent:
                    failure = failedEvent.Data as Exception
                        ?? new InvalidOperationException(
                            $"Agent workflow executor '{failedEvent.ExecutorId}' failed.");
                    break;
            }
        }

        if (failure is not null)
        {
            throw new AgentWorkflowExecutionException(
                $"Agent workflow step '{stepKey}' failed: {failure.Message}",
                failure);
        }
        if (!hasOutput)
        {
            throw new AgentWorkflowExecutionException(
                $"Agent workflow step '{stepKey}' completed without a compatible terminal result.");
        }
        return output!;
    }

    private static TOutput? ConvertOutput<TOutput>(WorkflowOutputEvent outputEvent)
    {
        if (outputEvent.As<TOutput>() is { } direct)
            return direct;
        if (outputEvent.Data is JsonElement element)
            return JsonSerializer.Deserialize<TOutput>(
                element.GetRawText(),
                SerializerOptions);
        return default;
    }
}
