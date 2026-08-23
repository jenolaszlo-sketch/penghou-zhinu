using System.Text.Json;

namespace Penghou.Zhinu.Declarative;

/// <summary>Executes a CompiledWorkflowDefinition through the existing durable runtime.</summary>
internal sealed class DeclarativeWorkflow : IWorkflow<JsonElement, JsonElement>
{
    private readonly CompiledWorkflowDefinition compiled;
    private readonly IActivityCatalogue catalogue;

    public DeclarativeWorkflow(CompiledWorkflowDefinition compiled, IActivityCatalogue catalogue)
    {
        ArgumentNullException.ThrowIfNull(compiled);
        ArgumentNullException.ThrowIfNull(catalogue);
        this.compiled = compiled;
        this.catalogue = catalogue;
    }

    public async Task<JsonElement> RunAsync(WorkflowContext context, JsonElement input, CancellationToken cancellationToken)
    {
        var outputs = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var inputJson = input;

        // For minimal vertical, steps are sequential A->B->C, each step's input is previous output (or workflow input for first)
        var orderedSteps = compiled.Steps.OrderBy(s => s.Id, StringComparer.Ordinal).ToList();
        // Simple topological sort for sequential dependencies
        var executed = new HashSet<string>(StringComparer.Ordinal);
        var remaining = new Queue<CompiledWorkflowStep>(orderedSteps);

        while (remaining.Count > 0)
        {
            var step = remaining.Dequeue();
            if (step.DependsOn.Any(d => !executed.Contains(d)))
            {
                remaining.Enqueue(step);
                continue;
            }

            var stepInput = step.DependsOn.Count == 0 ? inputJson : outputs[step.DependsOn[0]];
            var descriptor = step.Descriptor;
            var executor = catalogue.Resolve(step.Activity);

            var output = await context.StepAsync(
                step.Id,
                stepInput,
                async (JsonElement inp, CancellationToken ct) =>
                {
                    // Convert JsonElement input to the activity's expected CLR type
                    var inputType = executor.InputType;
                    object? typedInput;
                    if (inputType == typeof(JsonElement))
                        typedInput = inp;
                    else if (inputType == typeof(string) && inp.ValueKind == JsonValueKind.String)
                        typedInput = inp.GetString();
                    else
                        typedInput = JsonSerializer.Deserialize(inp.GetRawText(), inputType);

                    var result = await executor.ExecuteAsync(typedInput, ct);
                    // Normalize result to JsonElement
                    if (result is JsonElement je) return je;
                    if (result is string s) return JsonSerializer.Deserialize<JsonElement>($"\"{s}\"");
                    var json = JsonSerializer.Serialize(result, result?.GetType() ?? typeof(object));
                    return JsonSerializer.Deserialize<JsonElement>(json);
                },
                cancellationToken: cancellationToken);

            outputs[step.Id] = output;
            executed.Add(step.Id);
        }

        // Return last step's output as workflow output
        var lastStepId = orderedSteps.Last().Id;
        return outputs[lastStepId];
    }
}
