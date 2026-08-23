namespace Penghou.Zhinu.Declarative;

public sealed record WorkflowCompilationResult
{
    public bool IsValid => Compiled is not null;
    public CompiledWorkflowDefinition? Compiled { get; init; }
    public required IReadOnlyList<WorkflowValidationDiagnostic> Diagnostics { get; init; }
}

public static class WorkflowCompiler
{
    public static WorkflowCompilationResult Compile(
        DeclarativeWorkflowDefinition definition,
        ActivityCatalogue catalogue)
    {
        var validation = WorkflowDefinitionValidator.Validate(definition, catalogue);
        if (!validation.IsValid)
            return new WorkflowCompilationResult { Compiled = null, Diagnostics = validation.Diagnostics };

        var compiledSteps = new List<CompiledWorkflowStep>();
        foreach (var step in definition.Steps.OrderBy(s => s.Id, StringComparer.Ordinal))
        {
            var descriptor = catalogue.GetDescriptor(step.Activity);
            compiledSteps.Add(new CompiledWorkflowStep
            {
                Id = step.Id,
                Activity = step.Activity,
                DependsOn = (step.DependsOn ?? Array.Empty<string>()).OrderBy(d => d, StringComparer.Ordinal).ToArray(),
                Descriptor = descriptor
            });
        }

        var tempCompiled = new CompiledWorkflowDefinition
        {
            Name = definition.Name,
            Version = definition.Version,
            Fingerprint = string.Empty,
            Steps = compiledSteps
        };
        var canonical = WorkflowCanonicalizer.Canonicalize(tempCompiled);
        var fingerprint = WorkflowFingerprint.Compute(canonical);

        var compiled = new CompiledWorkflowDefinition
        {
            Name = definition.Name,
            Version = definition.Version,
            Fingerprint = fingerprint,
            Steps = compiledSteps
        };

        return new WorkflowCompilationResult { Compiled = compiled, Diagnostics = Array.Empty<WorkflowValidationDiagnostic>() };
    }
}
