using System.Text.Json;
using System.Text.Json.Serialization;

namespace Penghou.Zhinu.Declarative;

internal static class WorkflowCanonicalizer
{
    public static string Canonicalize(DeclarativeWorkflowDefinition definition)
    {
        var canonical = new
        {
            name = definition.Name,
            version = definition.Version,
            steps = definition.Steps
                .OrderBy(s => s.Id, StringComparer.Ordinal)
                .Select(s => new
                {
                    id = s.Id,
                    activity = new { name = s.Activity.Name, version = s.Activity.Version },
                    dependsOn = (s.DependsOn ?? Array.Empty<string>()).OrderBy(d => d, StringComparer.Ordinal).ToArray()
                }).ToArray()
        };
        var options = new JsonSerializerOptions
        {
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
        return JsonSerializer.Serialize(canonical, options);
    }

    public static string Canonicalize(CompiledWorkflowDefinition compiled)
    {
        var canonical = new
        {
            name = compiled.Name,
            version = compiled.Version,
            steps = compiled.Steps
                .OrderBy(s => s.Id, StringComparer.Ordinal)
                .Select(s => new
                {
                    id = s.Id,
                    activity = new { name = s.Activity.Name, version = s.Activity.Version },
                    dependsOn = s.DependsOn.OrderBy(d => d, StringComparer.Ordinal).ToArray(),
                    inputContract = s.Descriptor.Input.TypeId,
                    outputContract = s.Descriptor.Output.TypeId
                }).ToArray()
        };
        var options = new JsonSerializerOptions
        {
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
        return JsonSerializer.Serialize(canonical, options);
    }
}
