namespace Penghou.Zhinu;

/// <summary>
/// Creates an isolated step lease for one execution or compensation attempt.
/// Implementations own the activation scope and release it through the lease.
/// </summary>
public interface IWorkflowStepResolver
{
    ValueTask<IWorkflowStepLease<TStep>> ResolveAsync<TStep>(
        StepImplementationKey implementationKey,
        CancellationToken cancellationToken)
        where TStep : class;
}

/// <summary>Owns a resolved step and its attempt-scoped resources.</summary>
public interface IWorkflowStepLease<out TStep> : IAsyncDisposable
    where TStep : class
{
    TStep Step { get; }
}

internal sealed class UnavailableWorkflowStepResolver : IWorkflowStepResolver
{
    public static readonly UnavailableWorkflowStepResolver Instance = new();

    private UnavailableWorkflowStepResolver()
    {
    }

    public ValueTask<IWorkflowStepLease<TStep>> ResolveAsync<TStep>(
        StepImplementationKey implementationKey,
        CancellationToken cancellationToken)
        where TStep : class =>
        ValueTask.FromException<IWorkflowStepLease<TStep>>(
            new WorkflowConfigurationException(
                $"Class-based workflow step '{implementationKey}' requires a registered workflow step resolver. " +
                "Use Penghou.Zhinu.Hosting AddZhinu() or construct the engine with compatible hosting integration."));
}
