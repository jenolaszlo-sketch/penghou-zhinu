namespace Penghou.Zhinu;

/// <summary>Controls embedded workflow execution and polling behavior.</summary>
public sealed class ZhinuOptions
{
    public int MaxConcurrentWorkflows { get; set; } = 4;

    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan LeaseRenewalInterval { get; set; } = TimeSpan.FromSeconds(10);

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Minimum interval between expired-lease recovery sweeps. Recovery is also
    /// always performed once on initialization, so this only throttles the
    /// repeated sweeps issued by background scan loops.
    /// </summary>
    public TimeSpan LeaseRecoveryInterval { get; set; } = TimeSpan.FromSeconds(30);

    public int ScanBatchSize { get; set; } = 100;

    /// <summary>
    /// Maximum depth of child workflows executed inline by a parent. Deeper
    /// children are left for the poll loop / background host to drive.
    /// </summary>
    public int MaxNestingDepth { get; set; } = 16;

    /// <summary>
    /// Application policies evaluated before any artifact reference is
    /// persisted. Validators run in registration order.
    /// </summary>
    public IList<IWorkflowArtifactValidator> ArtifactValidators { get; } =
        new List<IWorkflowArtifactValidator>();

    internal ZhinuOptions Clone()
    {
        var clone = new ZhinuOptions
        {
            MaxConcurrentWorkflows = MaxConcurrentWorkflows,
            LeaseDuration = LeaseDuration,
            LeaseRenewalInterval = LeaseRenewalInterval,
            PollInterval = PollInterval,
            LeaseRecoveryInterval = LeaseRecoveryInterval,
            ScanBatchSize = ScanBatchSize,
            MaxNestingDepth = MaxNestingDepth
        };
        foreach (var v in ArtifactValidators) clone.ArtifactValidators.Add(v);
        return clone;
    }

    public void Validate()
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
        if (LeaseRecoveryInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(LeaseRecoveryInterval));
        if (ScanBatchSize < 1)
            throw new ArgumentOutOfRangeException(nameof(ScanBatchSize));
        if (MaxNestingDepth < 1)
            throw new ArgumentOutOfRangeException(nameof(MaxNestingDepth));
        if (ArtifactValidators.Any(validator => validator is null))
            throw new ArgumentException("Artifact validators must not contain null.", nameof(ArtifactValidators));
    }
}
