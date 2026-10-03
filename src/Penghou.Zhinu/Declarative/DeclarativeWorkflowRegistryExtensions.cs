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
        IActivityCatalogue catalogue)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(catalogue);

        if (catalogue is not IActivityExecutorResolver executorResolver)
        {
            throw new WorkflowConfigurationException(
                "The activity catalogue supports compilation but does not provide runtime activity execution.");
        }

        foreach (var step in definition.Steps)
        {
            var registeredDescriptor = catalogue.GetDescriptor(step.Activity);
            if (!DescriptorMatches(registeredDescriptor, step.Descriptor))
            {
                throw new ArgumentException(
                    $"Compiled contract for step '{step.Id}' does not match registered activity '{step.Activity}'.",
                    nameof(definition));
            }
        }

        CompiledWorkflowDefinitionValidator.Validate(definition, catalogue);

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

        return registry.Register(
            definition.Name,
            definition.Version,
            new DeclarativeWorkflow(definition, executorResolver));
    }

    private static bool DescriptorMatches(ActivityDescriptor registered, ActivityDescriptor compiled)
    {
        if (registered.Reference != compiled.Reference ||
            registered.Input.TypeId != compiled.Input.TypeId ||
            registered.Output.TypeId != compiled.Output.TypeId)
        {
            return false;
        }

        var left = registered.Authorization;
        var right = compiled.Authorization;
        if (left is null || right is null)
            return left is null && right is null;

        return left.PlanId == right.PlanId &&
               left.PlanRevision == right.PlanRevision &&
               left.Requirements.SequenceEqual(right.Requirements);
    }
}
