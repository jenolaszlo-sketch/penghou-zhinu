using System.Text.Json;

namespace Penghou.Zhinu;

/// <summary>Adapts registered typed workflow code to durable serialized runs.</summary>
public interface IWorkflowRegistration
{
    WorkflowDefinition Definition { get; }

    Type InputType { get; }

    Type OutputType { get; }

    string SerializeInput(object? input, JsonSerializerOptions options);

    Task<string?> ExecuteAsync(
        WorkflowContext context,
        string inputJson,
        JsonSerializerOptions options,
        CancellationToken cancellationToken);
}
