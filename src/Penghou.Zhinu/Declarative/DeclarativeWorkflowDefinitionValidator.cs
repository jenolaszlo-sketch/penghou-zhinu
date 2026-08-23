namespace Penghou.Zhinu.Declarative;

internal static class WorkflowDefinitionValidator
{
    public static WorkflowValidationResult Validate(DeclarativeWorkflowDefinition definition, IActivityCatalogue catalogue)
    {
        var diagnostics = new List<WorkflowValidationDiagnostic>();

        if (string.IsNullOrWhiteSpace(definition.Name))
            diagnostics.Add(new WorkflowValidationDiagnostic { Code = "WF001", Severity = WorkflowValidationSeverity.Error, Message = "Workflow name is required." });
        if (string.IsNullOrWhiteSpace(definition.Version))
            diagnostics.Add(new WorkflowValidationDiagnostic { Code = "WF002", Severity = WorkflowValidationSeverity.Error, Message = "Workflow version is required." });
        if (definition.Steps is null || definition.Steps.Count == 0)
            diagnostics.Add(new WorkflowValidationDiagnostic { Code = "WF010", Severity = WorkflowValidationSeverity.Error, Message = "Workflow must contain at least one executable step." });

        if (diagnostics.Any(d => d.Severity == WorkflowValidationSeverity.Error))
            return new WorkflowValidationResult { IsValid = false, Diagnostics = diagnostics };

        var stepIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var step in definition.Steps!)
        {
            if (string.IsNullOrWhiteSpace(step.Id))
                diagnostics.Add(new WorkflowValidationDiagnostic { Code = "WF011", Severity = WorkflowValidationSeverity.Error, Message = "Step ID is required.", StepId = step.Id });
            else if (!stepIds.Add(step.Id))
                diagnostics.Add(new WorkflowValidationDiagnostic { Code = "WF012", Severity = WorkflowValidationSeverity.Error, Message = $"Duplicate step ID '{step.Id}'.", StepId = step.Id });

            if (step.Activity is null)
                diagnostics.Add(new WorkflowValidationDiagnostic { Code = "WF013", Severity = WorkflowValidationSeverity.Error, Message = "Step activity reference is required.", StepId = step.Id });
        }

        var stepIdSet = new HashSet<string>(definition.Steps.Select(s => s.Id), StringComparer.Ordinal);
        foreach (var step in definition.Steps)
        {
            foreach (var dep in step.DependsOn ?? Array.Empty<string>())
            {
                if (!stepIdSet.Contains(dep))
                    diagnostics.Add(new WorkflowValidationDiagnostic { Code = "WF020", Severity = WorkflowValidationSeverity.Error, Message = $"Step '{step.Id}' depends on unknown step '{dep}'.", StepId = step.Id });
                if (dep == step.Id)
                    diagnostics.Add(new WorkflowValidationDiagnostic { Code = "WF021", Severity = WorkflowValidationSeverity.Error, Message = $"Step '{step.Id}' cannot depend on itself.", StepId = step.Id });
            }
        }

        // Check for cycles before validating the currently supported linear shape.
        var hasCycle = HasCycle(definition.Steps);
        if (hasCycle)
            diagnostics.Add(new WorkflowValidationDiagnostic { Code = "WF022", Severity = WorkflowValidationSeverity.Error, Message = "Dependency graph contains a cycle." });

        if (!hasCycle && !diagnostics.Any(d => d.Code is "WF012" or "WF020" or "WF021"))
            ValidateLinearTopology(definition.Steps, diagnostics);

        // Validate activity references and contracts
        var descriptors = new Dictionary<string, ActivityDescriptor>(StringComparer.Ordinal);
        foreach (var step in definition.Steps)
        {
            if (step.Activity is null) continue;
            if (!catalogue.TryGetDescriptor(step.Activity, out var descriptor))
            {
                diagnostics.Add(new WorkflowValidationDiagnostic { Code = "WF030", Severity = WorkflowValidationSeverity.Error, Message = $"Unknown activity '{step.Activity}'.", StepId = step.Id });
                continue;
            }
            descriptors[step.Id] = descriptor;
        }

        foreach (var step in definition.Steps)
        {
            var dependencyId = step.DependsOn?.Count == 1 ? step.DependsOn[0] : null;
            if (dependencyId is null ||
                !descriptors.TryGetValue(step.Id, out var descriptor) ||
                !descriptors.TryGetValue(dependencyId, out var dependencyDescriptor))
                continue;

            var previousOutputType = dependencyDescriptor.Output.ClrType;
            if (descriptor.Input.ClrType != typeof(object) &&
                !descriptor.Input.ClrType.IsAssignableFrom(previousOutputType))
            {
                diagnostics.Add(new WorkflowValidationDiagnostic { Code = "WF031", Severity = WorkflowValidationSeverity.Error, Message = $"Step '{step.Id}' input type '{descriptor.Input.ClrType.Name}' is not compatible with dependency '{dependencyId}' output '{previousOutputType.Name}'.", StepId = step.Id });
            }
        }

        return new WorkflowValidationResult { IsValid = !diagnostics.Any(d => d.Severity == WorkflowValidationSeverity.Error), Diagnostics = diagnostics };
    }

    private static void ValidateLinearTopology(
        IReadOnlyList<DeclarativeWorkflowStep> steps,
        List<WorkflowValidationDiagnostic> diagnostics)
    {
        var roots = steps.Where(s => (s.DependsOn?.Count ?? 0) == 0).ToArray();
        var invalidDependencyCounts = steps.Where(s => (s.DependsOn?.Count ?? 0) > 1).ToArray();
        var dependentCounts = steps
            .SelectMany(s => s.DependsOn ?? Array.Empty<string>())
            .GroupBy(id => id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var branchingSteps = steps.Where(s => dependentCounts.GetValueOrDefault(s.Id) > 1).ToArray();
        var sinks = steps.Where(s => dependentCounts.GetValueOrDefault(s.Id) == 0).ToArray();

        if (roots.Length != 1 || sinks.Length != 1 ||
            invalidDependencyCounts.Length != 0 || branchingSteps.Length != 0)
        {
            diagnostics.Add(new WorkflowValidationDiagnostic
            {
                Code = "WF023",
                Severity = WorkflowValidationSeverity.Error,
                Message = "The current declarative runtime supports one linear chain only: exactly one root and sink, one predecessor per non-root step, and one successor per non-sink step."
            });
        }
    }

    private static bool HasCycle(IReadOnlyList<DeclarativeWorkflowStep> steps)
    {
        var graph = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var nodes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var step in steps)
        {
            nodes.Add(step.Id);
            if (!graph.ContainsKey(step.Id)) graph[step.Id] = new List<string>();
            foreach (var dep in step.DependsOn ?? Array.Empty<string>())
            {
                nodes.Add(dep);
                if (!graph.ContainsKey(dep)) graph[dep] = new List<string>();
                graph[dep].Add(step.Id);
            }
        }
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        bool Dfs(string node)
        {
            if (visiting.Contains(node)) return true;
            if (visited.Contains(node)) return false;
            visiting.Add(node);
            foreach (var next in graph.GetValueOrDefault(node, new List<string>()))
                if (Dfs(next)) return true;
            visiting.Remove(node);
            visited.Add(node);
            return false;
        }
        foreach (var n in nodes) if (Dfs(n)) return true;
        return false;
    }
}
