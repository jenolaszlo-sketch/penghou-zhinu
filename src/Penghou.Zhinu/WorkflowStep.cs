namespace Penghou.Zhinu;

/// <summary>
/// Convenience base for class-based workflow steps.
/// </summary>
public abstract class WorkflowStep<TInput, TOutput> : IWorkflowStep<TInput, TOutput>
{
    public abstract Task<TOutput> ExecuteAsync(
        WorkflowStepContext context,
        TInput input,
        CancellationToken cancellationToken);
}

/// <summary>
/// Convenience base for class-based workflow steps that explicitly support
/// durable compensation. Workflows must still enable compensation at the
/// invocation site.
/// </summary>
public abstract class CompensatingWorkflowStep<TInput, TOutput> :
    WorkflowStep<TInput, TOutput>,
    ICompensatingWorkflowStep<TInput, TOutput>
{
    public abstract Task CompensateAsync(
        WorkflowStepContext context,
        TInput input,
        TOutput output,
        CancellationToken cancellationToken);
}
