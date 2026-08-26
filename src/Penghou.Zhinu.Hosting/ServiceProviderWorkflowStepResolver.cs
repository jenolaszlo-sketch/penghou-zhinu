using Microsoft.Extensions.DependencyInjection;

namespace Penghou.Zhinu.Hosting;

internal sealed class ServiceProviderWorkflowStepResolver(
    IServiceScopeFactory scopeFactory) : IWorkflowStepResolver
{
    public async ValueTask<IWorkflowStepLease<TStep>> ResolveAsync<TStep>(
        StepImplementationKey implementationKey,
        CancellationToken cancellationToken)
        where TStep : class
    {
        cancellationToken.ThrowIfCancellationRequested();
        var scope = scopeFactory.CreateAsyncScope();
        try
        {
            var matches = scope.ServiceProvider
                .GetKeyedServices<TStep>(implementationKey)
                .Take(2)
                .ToArray();
            if (matches.Length == 0)
            {
                throw new WorkflowConfigurationException(
                    $"Could not resolve workflow step '{implementationKey}' as '{typeof(TStep).FullName}'.");
            }
            if (matches.Length > 1)
            {
                throw new WorkflowConfigurationException(
                    $"Multiple workflow steps are registered for key '{implementationKey}' " +
                    $"and contract '{typeof(TStep).FullName}'. Register exactly one implementation.");
            }
            var step = matches[0];
            return new ServiceProviderWorkflowStepLease<TStep>(scope, step);
        }
        catch (Exception exception)
        {
            try
            {
                await scope.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception disposalException)
            {
                throw new WorkflowConfigurationException(
                    $"Resolving workflow step '{implementationKey}' as '{typeof(TStep).FullName}' failed, " +
                    "and disposing its resolution scope also failed.",
                    new AggregateException(exception, disposalException));
            }
            if (exception is WorkflowConfigurationException)
                throw;
            throw new WorkflowConfigurationException(
                $"Could not resolve workflow step '{implementationKey}' as '{typeof(TStep).FullName}'.",
                exception);
        }
    }

    private sealed class ServiceProviderWorkflowStepLease<TStep>(
        AsyncServiceScope scope,
        TStep step) : IWorkflowStepLease<TStep>
        where TStep : class
    {
        public TStep Step { get; } = step;

        public ValueTask DisposeAsync() => scope.DisposeAsync();
    }
}
