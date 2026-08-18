namespace Penghou.Zhinu;

/// <summary>
/// Durable registration metadata for a step compensation, carried from the
/// workflow definition through <see cref="StepClaimRequest"/> into the
/// compensation store. The compensating delegate itself is not persisted; the
/// workflow re-registers it on replay and dispatch is keyed by
/// <see cref="Name"/>.
/// </summary>
public sealed record CompensationMetadata(
    string Name,
    string RetryPolicyJson,
    TimeSpan? ExecutionTimeout);
