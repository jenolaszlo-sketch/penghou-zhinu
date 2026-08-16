namespace Penghou.Zhinu;

/// <summary>Describes the result of atomically attempting to claim a step.</summary>
public enum StepClaimDisposition
{
    Acquired,
    Reused,
    Waiting,
    Busy,
    Failed,
    Cancelled
}
