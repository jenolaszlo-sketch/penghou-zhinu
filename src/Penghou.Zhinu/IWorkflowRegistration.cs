using System.Text.Json;

namespace Penghou.Zhinu;

/// <summary>Adapts registered typed workflow code to durable serialized runs.</summary>
public interface IWorkflowRegistration
{
    WorkflowDefinition Definition { get; }

    Type InputType { get; }

    Type OutputType { get; }

    /// <summary>
    /// Deterministic fingerprint of the workflow definition when the registered
    /// workflow carries one (for example a compiled declarative definition), or
    /// null for ordinary code-first workflows. Stored on runs and verified on
    /// resume so a changed definition cannot silently replay an older run.
    /// </summary>
    string? DefinitionFingerprint { get; }

    string SerializeInput(object? input, JsonSerializerOptions options);

    Task<string?> ExecuteAsync(
        WorkflowContext context,
        string inputJson,
        JsonSerializerOptions options,
        CancellationToken cancellationToken);
}
