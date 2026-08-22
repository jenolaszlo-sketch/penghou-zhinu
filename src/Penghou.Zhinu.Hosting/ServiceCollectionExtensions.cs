using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using System.Reflection;
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
            provider.GetService<ILogger<WorkflowEngine>>(),
            provider.GetService<IWorkflowEventPublisher>()));
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

    /// <summary>Registers all concrete <see cref="IWorkflow{TInput,TOutput}"/> types from an assembly using naming convention.</summary>
    public static IServiceCollection AddZhinuWorkflowsFromAssembly(
        this IServiceCollection services,
        Assembly assembly,
        Func<Type, string>? nameSelector = null,
        Func<Type, string>? versionSelector = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(assembly);
        nameSelector ??= t => t.Name.EndsWith("Workflow", StringComparison.Ordinal)
            ? t.Name[..^8].ToLowerInvariant()
            : t.Name.ToLowerInvariant();
        versionSelector ??= _ => "1";

        foreach (var type in assembly.GetExportedTypes())
        {
            if (type.IsAbstract || type.IsInterface) continue;
            var workflowInterface = type.GetInterfaces()
                .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IWorkflow<,>));
            if (workflowInterface is null) continue;

            var args = workflowInterface.GetGenericArguments();
            var inputType = args[0];
            var outputType = args[1];
            var name = nameSelector(type);
            var version = versionSelector(type);
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            ArgumentException.ThrowIfNullOrWhiteSpace(version);

            var method = typeof(ServiceCollectionExtensions)
                .GetMethod(nameof(AddZhinuWorkflow), BindingFlags.Public | BindingFlags.Static)!
                .MakeGenericMethod(type, inputType, outputType);
            method.Invoke(null, [services, name, version]);
        }

        return services;
    }
}
