namespace Penghou.Zhinu;

/// <summary>Validates dependency graphs for cycles.</summary>
public static class WorkflowDependencyValidator
{
    public static bool HasCycle(IReadOnlyList<StepDependency> edges)
    {
        var graph = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var nodes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in edges)
        {
            nodes.Add(e.StepKey);
            nodes.Add(e.DependsOnStepKey);
            if (!graph.TryGetValue(e.DependsOnStepKey, out var list))
                graph[e.DependsOnStepKey] = list = [];
            list.Add(e.StepKey);
            if (!graph.ContainsKey(e.StepKey)) graph[e.StepKey] = [];
        }

        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);

        bool Dfs(string node)
        {
            if (visiting.Contains(node)) return true;
            if (visited.Contains(node)) return false;
            visiting.Add(node);
            foreach (var next in graph.GetValueOrDefault(node, []))
                if (Dfs(next)) return true;
            visiting.Remove(node);
            visited.Add(node);
            return false;
        }

        foreach (var n in nodes)
            if (Dfs(n)) return true;
        return false;
    }
}
