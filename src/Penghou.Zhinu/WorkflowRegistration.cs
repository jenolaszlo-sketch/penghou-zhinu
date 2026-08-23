using System.Text.Json;

namespace Penghou.Zhinu;

/// <summary>Registers one typed workflow using a caller-provided factory.</summary>
public sealed class WorkflowRegistration<TInput, TOutput>
    : IWorkflowRegistration
{
    private readonly Func<IWorkflow<TInput, TOutput>> workflowFactory;
    private readonly Lazy<string?> definitionFingerprint;

    public WorkflowRegistration(
        WorkflowDefinition definition,
        Func<IWorkflow<TInput, TOutput>> workflowFactory)
    {
        Definition = ValidateDefinition(definition);
        this.workflowFactory = workflowFactory ??
            throw new ArgumentNullException(nameof(workflowFactory));
        definitionFingerprint = new Lazy<string?>(
            () => (workflowFactory() as IWorkflowFingerprint)?.Fingerprint);
    }

    public WorkflowDefinition Definition { get; }

    public string? DefinitionFingerprint => definitionFingerprint.Value;

    public Type InputType => typeof(TInput);

    public Type OutputType => typeof(TOutput);

    public string SerializeInput(object? input, JsonSerializerOptions options)
    {
        if (input is not TInput typedInput && input is not null)
            throw new ArgumentException(
                $"Workflow input must be assignable to {typeof(TInput)}.",
                nameof(input));
        return JsonSerializer.Serialize((TInput?)input, options);
    }

    public async Task<string?> ExecuteAsync(
        WorkflowContext context,
        string inputJson,
        JsonSerializerOptions options,
        CancellationToken cancellationToken)
    {
        var input = JsonSerializer.Deserialize<TInput>(inputJson, options);
        if (input is null && default(TInput) is not null)
            throw new WorkflowSerializationException(
                $"Workflow input could not be deserialized as {typeof(TInput)}.");
        var output = await workflowFactory().RunAsync(
            context,
            input!,
            cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(output, options);
    }

    private static WorkflowDefinition ValidateDefinition(
        WorkflowDefinition value)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentException.ThrowIfNullOrWhiteSpace(value.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(value.Version);
        return value;
    }
}
