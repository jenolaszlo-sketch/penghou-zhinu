using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Penghou.Zhinu.Hosting;

/// <summary>Registers the optional embedded hosted execution loop.</summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddZhinu(
        this IServiceCollection services,
        Action<ZhinuOptions>? configure = null,
        JsonSerializerOptions? serializerOptions = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = new ZhinuOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<WorkflowRegistry>(provider =>
        {
            var registry = new WorkflowRegistry();
            foreach (var registration in
                     provider.GetServices<IWorkflowRegistration>())
            {
                registry.Register(registration);
            }
            return registry;
        });
        services.TryAddSingleton<IWorkflowRegistry>(provider =>
            provider.GetRequiredService<WorkflowRegistry>());
        if (!services.Any(service =>
                service.ServiceType == typeof(IWorkflowStore) ||
                typeof(IWorkflowStore).IsAssignableFrom(service.ServiceType)))
        {
            throw new InvalidOperationException(
                "AddZhinu requires a registered IWorkflowStore. Register the " +
                "Penghou.Zhinu.Sqlite package with AddZhinuSqlite(...), or register " +
                "your own IWorkflowStore implementation, before calling AddZhinu.");
        }
        services.TryAddSingleton(provider => new WorkflowEngine(
            provider.GetRequiredService<IWorkflowStore>(),
            provider.GetRequiredService<IWorkflowRegistry>(),
            provider.GetRequiredService<ZhinuOptions>(),
            serializerOptions,
            provider.GetRequiredService<TimeProvider>(),
            provider.GetService<ILogger<WorkflowEngine>>()));
        services.AddHostedService<ZhinuHostedService>();
        return services;
    }

    public static IServiceCollection AddZhinuWorkflow<TWorkflow, TInput, TOutput>(
        this IServiceCollection services,
        string name,
        string version)
        where TWorkflow : class, IWorkflow<TInput, TOutput>
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        services.TryAddSingleton<TWorkflow>();
        services.AddSingleton<IWorkflowRegistration>(provider =>
            new WorkflowRegistration<TInput, TOutput>(
                new WorkflowDefinition { Name = name, Version = version },
                provider.GetRequiredService<TWorkflow>));
        return services;
    }
}
