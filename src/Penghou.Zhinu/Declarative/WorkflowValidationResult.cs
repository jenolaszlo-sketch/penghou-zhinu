namespace Penghou.Zhinu.Declarative;

internal enum WorkflowValidationSeverity
{
    Error,
    Warning
}

internal sealed record WorkflowValidationDiagnostic
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
