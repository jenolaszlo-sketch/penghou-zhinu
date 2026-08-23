namespace Penghou.Zhinu.Declarative;

/// <summary>Registers compiled declarative workflows with the durable runtime.</summary>
public static class DeclarativeWorkflowRegistryExtensions
{
    /// <summary>
    /// Validates the compiled artifact against its catalogue and registers its
    /// internal runtime adapter.
    /// </summary>
    public static WorkflowRegistry RegisterDeclarative(
        this WorkflowRegistry registry,
        CompiledWorkflowDefinition definition,
        ActivityCatalogue catalogue)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(catalogue);

        var computedFingerprint = WorkflowFingerprint.Compute(definition);
        if (!string.Equals(
            definition.Fingerprint,
            computedFingerprint,
            StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The compiled workflow fingerprint does not match its canonical content.",
                nameof(definition));
        }

        foreach (var step in definition.Steps)
        {
            var registeredDescriptor = catalogue.GetDescriptor(step.Activity);
            if (registeredDescriptor != step.Descriptor)
            {
                throw new ArgumentException(
                    $"Compiled contract for step '{step.Id}' does not match registered activity '{step.Activity}'.",
                    nameof(definition));
            }
        }

        return registry.Register(
            definition.Name,
            definition.Version,
            new DeclarativeWorkflow(definition, catalogue));
    }
}
