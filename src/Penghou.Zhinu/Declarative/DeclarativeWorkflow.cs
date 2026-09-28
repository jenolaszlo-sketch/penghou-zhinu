using System.Text.Json;

namespace Penghou.Zhinu.Declarative;

/// <summary>
/// Runtime JSON conversions for declarative activities. These deliberately use
/// the default options: converted values feed durable input hashes, so an
/// option change would silently reinterpret persisted step identities.
/// </summary>
internal static class DeclarativeJson
{
    public static System.Text.Json.JsonSerializerOptions Options =>
        System.Text.Json.JsonSerializerOptions.Default;
}

/// <summary>Executes a CompiledWorkflowDefinition through the existing durable runtime.</summary>
internal sealed class DeclarativeWorkflow : IWorkflow<JsonElement, JsonElement>, IWorkflowFingerprint
{
    private readonly CompiledWorkflowDefinition compiled;
    private readonly IActivityExecutorResolver catalogue;

    public DeclarativeWorkflow(CompiledWorkflowDefinition compiled, IActivityExecutorResolver catalogue)
    {
        ArgumentNullException.ThrowIfNull(compiled);
        ArgumentNullException.ThrowIfNull(catalogue);
        this.compiled = compiled;
        this.catalogue = catalogue;
    }

    public string Fingerprint => compiled.Fingerprint;

    public async Task<JsonElement> RunAsync(WorkflowContext context, JsonElement input, CancellationToken cancellationToken)
    {
        var outputs = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var inputJson = input;

        // For minimal vertical, steps are sequential A->B->C, each step's input is previous output (or workflow input for first)
        var orderedSteps = compiled.Steps.OrderBy(s => s.Id, StringComparer.Ordinal).ToList();
        // Simple topological sort for sequential dependencies
        var executed = new HashSet<string>(StringComparer.Ordinal);
        var remaining = new Queue<CompiledWorkflowStep>(orderedSteps);
        var lastExecutedId = string.Empty;

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
                        typedInput = JsonSerializer.Deserialize(
                            inp.GetRawText(), inputType, DeclarativeJson.Options);

                    var result = await executor.ExecuteAsync(typedInput, ct);
                    // Normalize result to JsonElement
                    if (result is JsonElement je) return je;
                    return JsonSerializer.SerializeToElement(
                        result,
                        result?.GetType() ?? typeof(object),
                        DeclarativeJson.Options);
                },
                new StepOptions { DependsOn = step.DependsOn },
                cancellationToken: cancellationToken);

            outputs[step.Id] = output;
            executed.Add(step.Id);
            lastExecutedId = step.Id;
        }

        // Return the unique sink's output: names are durable identities, not
        // execution order. Without a unique sink, fall back to the last
        // topologically executed step. Previously executed runs keep their
        // committed step rows and edges untouched; only new executions record
        // the declared edges.
        var sinks = orderedSteps
            .Where(candidate => !orderedSteps.Any(other => other.DependsOn.Contains(candidate.Id)))
            .ToList();
        if (sinks.Count == 1)
            return outputs[sinks[0].Id];
        if (lastExecutedId.Length == 0)
            throw new WorkflowStateException("Compiled workflow has no steps to execute.");
        return outputs[lastExecutedId];
    }
}
