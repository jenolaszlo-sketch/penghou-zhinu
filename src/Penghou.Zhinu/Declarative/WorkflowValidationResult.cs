namespace Penghou.Zhinu.Declarative;

public enum WorkflowValidationSeverity
{
    Error,
    Warning
}

public sealed record WorkflowValidationDiagnostic
{
    public required string Code { get; init; }
    public required WorkflowValidationSeverity Severity { get; init; }
    public required string Message { get; init; }
    public string? StepId { get; init; }
}

internal sealed record WorkflowValidationResult
{
    public required bool IsValid { get; init; }
    public required IReadOnlyList<WorkflowValidationDiagnostic> Diagnostics { get; init; }
}
