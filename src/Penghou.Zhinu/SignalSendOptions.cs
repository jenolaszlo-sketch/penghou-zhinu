namespace Penghou.Zhinu;

/// <summary>Options for a retry-safe external signal send.</summary>
public sealed class SignalSendOptions
{
    /// <summary>
    /// Stable caller-supplied identity for the logical signal. Identical retries
    /// return the committed receipt; conflicting reuse is rejected.
    /// </summary>
    public required Guid SignalId { get; init; }
}
