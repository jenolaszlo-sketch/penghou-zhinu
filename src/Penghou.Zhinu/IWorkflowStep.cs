namespace Penghou.Zhinu;

/// <summary>
/// Implements one reusable operation invoked by a workflow. Instances are
/// scoped to one execution or compensation attempt and must not retain durable
/// state in fields.
/// </summary>
public interface IWorkflowStep<TInput, TOutput>
{
    Task<TOutput> ExecuteAsync(
        WorkflowStepContext context,
        TInput input,
        CancellationToken cancellationToken);
}

/// <summary>
/// A workflow step that can compensate a previously committed execution.
/// </summary>
public interface ICompensatingWorkflowStep<TInput, TOutput> :
    IWorkflowStep<TInput, TOutput>
{
    Task CompensateAsync(
        WorkflowStepContext context,
        TInput input,
        TOutput output,
        CancellationToken cancellationToken);
}
