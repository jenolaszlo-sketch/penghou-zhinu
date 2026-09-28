namespace Penghou.Zhinu;

/// <summary>Bounds keyed fan-out execution.</summary>
public sealed record FanOutOptions
{
    /// <summary>Maximum items executing at once. Chunked scheduling also bounds outstanding task objects.</summary>
    public required int MaxDegreeOfParallelism { get; init; }

    internal void Validate()
    {
        if (MaxDegreeOfParallelism < 1)
            throw new ArgumentOutOfRangeException(nameof(MaxDegreeOfParallelism));
    }
}
