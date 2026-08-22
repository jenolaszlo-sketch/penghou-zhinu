using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using System.Text.Json;

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
        Workflow workflow,
        TInput input,
        ICheckpointStore<JsonElement> checkpointStore,
        CancellationToken cancellationToken = default)
        where TInput : notnull
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentNullException.ThrowIfNull(checkpointStore);
        ArgumentException.ThrowIfNullOrWhiteSpace(stepKey);
        return context.StepAsync(
            stepKey,
            input,
            (_, ct) => ExecuteAsync<TInput, TOutput>(
                context.WorkflowRunId,
                stepKey,
                workflow,
                input,
                checkpointStore,
                ct),
            null,
            cancellationToken);
    }

    private static async Task<TOutput> ExecuteAsync<TInput, TOutput>(
        Guid workflowRunId,
        string stepKey,
        Workflow workflow,
        TInput input,
        ICheckpointStore<JsonElement> checkpointStore,
        CancellationToken cancellationToken)
        where TInput : notnull
    {
        var sessionId = $"{workflowRunId:D}:{stepKey}";
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
        Exception? failure = null;
        await foreach (var evt in run.WatchStreamAsync()
            .WithCancellation(cancellationToken))
        {
            switch (evt)
            {
                case WorkflowOutputEvent outputEvent:
                    output = ConvertOutput<TOutput>(outputEvent);
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
