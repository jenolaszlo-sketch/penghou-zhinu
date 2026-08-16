namespace Penghou.Zhinu;

/// <summary>Controls embedded workflow execution and polling behavior.</summary>
public sealed class ZhinuOptions
{
    public int MaxConcurrentWorkflows { get; set; } = 4;

    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan LeaseRenewalInterval { get; set; } = TimeSpan.FromSeconds(10);

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(250);

    public int ScanBatchSize { get; set; } = 100;

    internal void Validate()
    {
        if (MaxConcurrentWorkflows < 1)
            throw new ArgumentOutOfRangeException(nameof(MaxConcurrentWorkflows));
        if (LeaseDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(LeaseDuration));
        if (LeaseRenewalInterval <= TimeSpan.Zero ||
            LeaseRenewalInterval >= LeaseDuration)
        {
            throw new ArgumentOutOfRangeException(nameof(LeaseRenewalInterval));
        }
        if (PollInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(PollInterval));
        if (ScanBatchSize < 1)
            throw new ArgumentOutOfRangeException(nameof(ScanBatchSize));
    }
}
