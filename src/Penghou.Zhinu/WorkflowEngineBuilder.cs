using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Penghou.Zhinu;

/// <summary>Fluent builder for <see cref="WorkflowEngine"/> avoiding constructor ordering mistakes.</summary>
public sealed class WorkflowEngineBuilder
{
    private IWorkflowStore? store;
    private IWorkflowRegistry? registry;
    private ZhinuOptions options = new();
    private JsonSerializerOptions? serializerOptions;
    private TimeProvider? timeProvider;
    private ILogger<WorkflowEngine>? logger;
    private IWorkflowEventPublisher? eventPublisher;
    private IWorkflowStepResolver? workflowStepResolver;

    public WorkflowEngineBuilder WithStore(IWorkflowStore value)
    {
        store = value ?? throw new ArgumentNullException(nameof(value));
        return this;
    }

    public WorkflowEngineBuilder WithRegistry(IWorkflowRegistry value)
    {
        registry = value ?? throw new ArgumentNullException(nameof(value));
        return this;
    }

    public WorkflowEngineBuilder WithOptions(ZhinuOptions value)
    {
        ArgumentNullException.ThrowIfNull(value);
        options = value.Clone();
        return this;
    }

    public WorkflowEngineBuilder WithOptions(Action<ZhinuOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(options);
        return this;
    }

    public WorkflowEngineBuilder WithSerializerOptions(JsonSerializerOptions value)
    {
        ArgumentNullException.ThrowIfNull(value);
        serializerOptions = ZhinuJsonDefaults.CloneAndFreeze(value);
        return this;
    }

    public WorkflowEngineBuilder WithTimeProvider(TimeProvider value)
    {
        timeProvider = value ?? throw new ArgumentNullException(nameof(value));
        return this;
    }

    public WorkflowEngineBuilder WithLogger(ILogger<WorkflowEngine> value)
    {
        logger = value ?? throw new ArgumentNullException(nameof(value));
        return this;
    }

    public WorkflowEngineBuilder WithEventPublisher(IWorkflowEventPublisher value)
    {
        eventPublisher = value ?? throw new ArgumentNullException(nameof(value));
        return this;
    }

    public WorkflowEngineBuilder WithStepResolver(IWorkflowStepResolver value)
    {
        workflowStepResolver = value ?? throw new ArgumentNullException(nameof(value));
        return this;
    }

    public WorkflowEngine Build()
    {
        if (store is null) throw new InvalidOperationException("Store must be configured.");
        if (registry is null) throw new InvalidOperationException("Registry must be configured.");
        return new WorkflowEngine(
            store,
            registry,
            options,
            serializerOptions,
            timeProvider,
            logger,
            eventPublisher,
            workflowStepResolver);
    }
}
