namespace Penghou.Zhinu.Declarative;

/// <summary>Minimal runtime surface required by compiled workflow execution.</summary>
internal interface IWorkflowRuntime
{
    Task<TOutput> ExecuteStepAsync<TInput, TOutput>(string stepId, TInput input, Func<TInput, CancellationToken, Task<TOutput>> activity, CancellationToken cancellationToken);
}
