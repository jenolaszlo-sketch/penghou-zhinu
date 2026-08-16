namespace Penghou.Zhinu;

/// <summary>Defines ordinary async workflow code executed at durable step boundaries.</summary>
public interface IWorkflow<in TInput, TOutput>
{
    Task<TOutput> RunAsync(
        WorkflowContext context,
        TInput input,
        CancellationToken cancellationToken);
}
