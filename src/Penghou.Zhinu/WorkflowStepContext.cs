namespace Penghou.Zhinu;

/// <summary>Provides stable identifiers for downstream idempotency.</summary>
public sealed record WorkflowStepContext(
    Guid WorkflowRunId,
    Guid StepExecutionId,
    string StepKey,
    int Attempt)
{
    public string IdempotencyKey =>
        $"workflow:{WorkflowRunId:D}:step:{StepKey}";
}
