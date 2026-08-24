namespace Penghou.Zhinu.Declarative;

internal static class CompiledWorkflowDefinitionValidator
{
    public static void Validate(
        CompiledWorkflowDefinition definition,
        IActivityCatalogue catalogue)
    {
        var source = new DeclarativeWorkflowDefinition
        {
            Name = definition.Name,
            Version = definition.Version,
            Steps = definition.Steps.Select(step => new DeclarativeWorkflowStep
            {
                Id = step.Id,
                Activity = step.Activity,
                DependsOn = step.DependsOn
            }).ToArray()
        };
        var result = WorkflowDefinitionValidator.Validate(source, catalogue);
        if (!result.IsValid)
        {
            var details = string.Join("; ", result.Diagnostics
                .Where(diagnostic => diagnostic.Severity == WorkflowValidationSeverity.Error)
                .Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}"));
            throw new ArgumentException(
                $"The compiled workflow definition is invalid: {details}",
                nameof(definition));
        }
    }
}
