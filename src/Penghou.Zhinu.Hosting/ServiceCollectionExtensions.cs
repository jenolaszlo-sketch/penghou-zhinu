using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Penghou.Workflow.Abstractions;
using System.Reflection;
using System.Text.Json;

namespace Penghou.Zhinu.Hosting;

/// <summary>Registers the optional embedded hosted execution loop.</summary>
public static class ServiceCollectionExtensions
{
    private sealed record AuthorizationRegistration(WorkflowExecutionAuthorizationOptions Options);
    private sealed record BaseOptionsRegistration(ZhinuOptions Options);

    private static ZhinuOptions CreateConfiguredOptions(IServiceProvider provider)
    {
        var configured = provider.GetRequiredService<BaseOptionsRegistration>().Options.Clone();
        var authorization = provider.GetService<AuthorizationRegistration>();
        if (authorization is not null)
            configured.ExecutionAuthorization = authorization.Options;
        return configured;
    }

    private static bool IsZhinuOwnedOptionsFactory(ServiceDescriptor descriptor) =>
        descriptor.ImplementationFactory?.Method ==
        ((Func<IServiceProvider, ZhinuOptions>)CreateConfiguredOptions).Method;

    private static ZhinuOptions ResolveZhinuOptions(IServiceProvider provider)
    {
        var options = provider.GetServices<ZhinuOptions>().ToArray();
        if (options.Length != 1)
            throw new InvalidOperationException("AddZhinu requires exactly one effective ZhinuOptions registration; duplicate or shadowing registrations are rejected.");
        return options[0];
    }

    /// <summary>Registers the one explicit authority configuration for this Zhinu host.</summary>
    public static IServiceCollection AddZhinuExecutionAuthorization(
        this IServiceCollection services,
        string providerId,
        string bindingId,
        IExecutionAuthorizer authorizer,
        IWorkflowAuthorizationEvidenceVerifier? evidenceVerifier = null,
        TimeSpan? evaluationTimeout = null,
        TimeSpan? maximumDecisionLifetime = null,
        TimeSpan? maximumClockSkew = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (services.Any(d => d.ServiceType == typeof(AuthorizationRegistration)))
            throw new InvalidOperationException("Zhinu execution authorization is already registered; exactly one provider is allowed.");
        var optionDescriptors = services.Where(d => d.ServiceType == typeof(ZhinuOptions)).ToArray();
        if (optionDescriptors.Length > 0 &&
            !optionDescriptors.All(IsZhinuOwnedOptionsFactory))
            throw new InvalidOperationException("A custom ZhinuOptions DI registration conflicts with AddZhinuExecutionAuthorization.");
        if (services.Any(d => d.ServiceType == typeof(Microsoft.Extensions.Options.IConfigureOptions<ZhinuOptions>) ||
                              d.ServiceType == typeof(Microsoft.Extensions.Options.IPostConfigureOptions<ZhinuOptions>)))
            throw new InvalidOperationException("Microsoft options configuration for ZhinuOptions conflicts with AddZhinuExecutionAuthorization.");

        var options = new WorkflowExecutionAuthorizationOptions(providerId, bindingId,
            authorizer, evidenceVerifier, evaluationTimeout, maximumDecisionLifetime, maximumClockSkew);
        services.AddSingleton(new AuthorizationRegistration(options));
        return services;
    }

    public static IServiceCollection AddZhinu(
        this IServiceCollection services,
        Action<ZhinuOptions>? configure = null,
        JsonSerializerOptions? serializerOptions = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = new ZhinuOptions();
        configure?.Invoke(options);
        options.Validate();
        if (options.ExecutionAuthorization is not null && services.Any(d => d.ServiceType == typeof(AuthorizationRegistration)))
            throw new InvalidOperationException("Configure execution authorization with AddZhinuExecutionAuthorization, not through AddZhinu's ZhinuOptions callback.");
        if (options.ExecutionAuthorization is not null)
            throw new InvalidOperationException("Configure execution authorization with AddZhinuExecutionAuthorization, not through AddZhinu's ZhinuOptions callback.");
        if (!services.Any(d => d.ServiceType == typeof(BaseOptionsRegistration)))
            services.AddSingleton(new BaseOptionsRegistration(options));
        if (services.Any(d => d.ServiceType == typeof(AuthorizationRegistration)) &&
            services.Any(d => d.ServiceType == typeof(ZhinuOptions) && !IsZhinuOwnedOptionsFactory(d)))
            throw new InvalidOperationException("A custom ZhinuOptions DI registration conflicts with AddZhinuExecutionAuthorization.");
        services.TryAddSingleton<ZhinuOptions>(CreateConfiguredOptions);
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
        services.TryAddSingleton<IWorkflowStepResolver,
            ServiceProviderWorkflowStepResolver>();
        if (!services.Any(service =>
                service.ServiceType == typeof(IWorkflowStore) ||
                (service.ImplementationType is not null && typeof(IWorkflowStore).IsAssignableFrom(service.ImplementationType)) ||
                (service.ImplementationInstance is not null && service.ImplementationInstance is IWorkflowStore)))
        {
            throw new InvalidOperationException(
                "AddZhinu requires a registered IWorkflowStore. Register the " +
                "Penghou.Zhinu.Sqlite package with AddZhinuSqlite(...), or register " +
                "your own IWorkflowStore implementation, before calling AddZhinu.");
        }
        services.TryAddSingleton(provider => new WorkflowEngine(
            provider.GetRequiredService<IWorkflowStore>(),
            provider.GetRequiredService<IWorkflowRegistry>(),
            ResolveZhinuOptions(provider),
            serializerOptions,
            provider.GetRequiredService<TimeProvider>(),
            provider.GetService<ILogger<WorkflowEngine>>(),
            provider.GetService<IWorkflowEventPublisher>(),
            provider.GetRequiredService<IWorkflowStepResolver>()));
        services.TryAddSingleton<IWorkflowRuntime>(provider =>
            provider.GetRequiredService<WorkflowEngine>());
        services.TryAddSingleton<IWorkflowClient>(provider =>
            provider.GetRequiredService<WorkflowEngine>());
        services.TryAddSingleton<IIdempotentWorkflowClient>(provider =>
            provider.GetRequiredService<WorkflowEngine>());
        services.TryAddSingleton<IWorkflowAdministration>(provider =>
            provider.GetRequiredService<WorkflowEngine>());
        services.TryAddSingleton<IWorkflowStarter>(provider =>
            provider.GetRequiredService<WorkflowEngine>());
        services.TryAddSingleton<IWorkflowReader>(provider =>
            provider.GetRequiredService<WorkflowEngine>());
        services.TryAddSingleton<IWorkflowOperator>(provider =>
            provider.GetRequiredService<WorkflowEngine>());
        services.TryAddSingleton<IHostedWorkflowRuntime>(provider =>
            provider.GetRequiredService<WorkflowEngine>());
        services.AddHostedService<ZhinuHostedService>();
        return services;
    }

    /// <summary>
    /// Registers a keyed class-based workflow step with scoped lifetime. Zhinu
    /// creates and asynchronously disposes a fresh scope for every execution or
    /// compensation attempt.
    /// </summary>
    public static IServiceCollection AddZhinuStep<TStep, TInput, TOutput>(
        this IServiceCollection services,
        StepImplementationKey implementationKey)
        where TStep : class, IWorkflowStep<TInput, TOutput>
    {
        ArgumentNullException.ThrowIfNull(services);
        implementationKey.Validate(nameof(implementationKey));
        var contract = typeof(IWorkflowStep<TInput, TOutput>);
        if (services.Any(service =>
                service.IsKeyedService &&
                service.ServiceType == contract &&
                Equals(service.ServiceKey, implementationKey)))
        {
            throw new WorkflowRegistrationException(
                $"A workflow step is already registered for key '{implementationKey}' " +
                $"and contract '{contract.FullName}'.");
        }
        services.AddKeyedScoped<IWorkflowStep<TInput, TOutput>, TStep>(
            implementationKey);
        return services;
    }

    /// <summary>
    /// Registers an implementation against a shared typed step reference.
    /// The implementation must implement the reference's exact input/output
    /// contract. A fresh scoped instance is resolved for every attempt.
    /// </summary>
    public static IServiceCollection AddZhinuStep<TStep>(
        this IServiceCollection services,
        WorkflowStepReference step)
        where TStep : class
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(step);
        step.ImplementationKey.Validate(nameof(step));
        var contract = typeof(IWorkflowStep<,>).MakeGenericType(
            step.InputType,
            step.OutputType);
        if (!contract.IsAssignableFrom(typeof(TStep)))
        {
            throw new WorkflowRegistrationException(
                $"Workflow step implementation '{typeof(TStep).FullName}' does not implement " +
                $"'{contract.FullName}' required by '{step.ImplementationKey}'.");
        }
        if (services.Any(service =>
                service.IsKeyedService &&
                service.ServiceType == contract &&
                Equals(service.ServiceKey, step.ImplementationKey)))
        {
            throw new WorkflowRegistrationException(
                $"A workflow step is already registered for key '{step.ImplementationKey}' " +
                $"and contract '{contract.FullName}'.");
        }

        services.AddKeyedScoped(contract, step.ImplementationKey, typeof(TStep));
        return services;
    }

    /// <summary>
    /// Registers a workflow implementation and its definition registration.
    /// The implementation is a singleton shared by every run: keep it
    /// stateless and put per-run state in step inputs, outputs, or scoped
    /// step dependencies instead of instance fields. Mutable per-run fields
    /// are shared across concurrent runs and are never persisted.
    /// </summary>
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
