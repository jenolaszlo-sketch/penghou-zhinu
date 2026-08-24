using System.Collections.Concurrent;

namespace Penghou.Zhinu;

/// <summary>Stores workflow registrations for direct or dependency-injected use.</summary>
public sealed class WorkflowRegistry : IWorkflowRegistry
{
    private readonly ConcurrentDictionary<(string Name, string Version),
        IWorkflowRegistration> registrations = new();

    public WorkflowRegistry Register(IWorkflowRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        var key = (
            registration.Definition.Name,
            registration.Definition.Version);
        if (!registrations.TryAdd(key, registration))
        {
            throw new WorkflowRegistrationException(
                $"Workflow '{key.Name}' version '{key.Version}' is already registered.");
        }
        return this;
    }

    public WorkflowRegistry Register<TInput, TOutput>(
        string name,
        string version,
        IWorkflow<TInput, TOutput> workflow) =>
        Register(new WorkflowRegistration<TInput, TOutput>(
            new WorkflowDefinition { Name = name, Version = version },
            () => workflow));

    public IWorkflowRegistration Get(string name, string version) =>
        TryGet(name, version, out var registration)
            ? registration!
            : throw new WorkflowDefinitionUnavailableException(name, version);

    public bool TryGet(
        string name,
        string version,
        out IWorkflowRegistration? registration) =>
        registrations.TryGetValue((name, version), out registration);
}
