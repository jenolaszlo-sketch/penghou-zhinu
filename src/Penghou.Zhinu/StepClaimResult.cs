namespace Penghou.Zhinu;

/// <summary>Returns the persisted step and the action the caller should take.</summary>
public sealed record StepClaimResult(
    StepClaimDisposition Disposition,
    WorkflowStepRun Step);
