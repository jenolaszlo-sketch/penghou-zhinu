namespace Penghou.Zhinu;

public sealed class WorkflowStepFailedException : Exception
{
    public WorkflowStepFailedException(
        string stepKey,
        WorkflowError error,
        Exception? innerException = null)
        : base($"Workflow step '{stepKey}' failed: {error.Message}", innerException)
    {
        StepKey = stepKey;
        Error = error;
    }

    public string StepKey { get; }

    public WorkflowError Error { get; }
}
