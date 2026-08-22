namespace Penghou.Zhinu;

/// <summary>Configures <see cref="WorkflowEngine.GetRunProgressAsync"/>.</summary>
public sealed record RunProgressOptions
{
    /// <summary>
    /// How many levels of child runs to include. The run itself is depth 0;
    /// direct children are depth 1. Defaults to 8.
    /// </summary>
    public int MaxDepth { get; init; } = 8;

    /// <summary>Whether to include each run's recent diagnostic events. Defaults to true.</summary>
    public bool IncludeEvents { get; init; } = true;

    /// <summary>Maximum events to include per run. Defaults to 100.</summary>
    public int EventsLimit { get; init; } = 100;

    public bool IncludeArtifacts { get; init; } = true;

    public bool IncludeDiagnosis { get; init; } = true;

    public bool IncludeActiveOperation { get; init; } = true;

    public bool IncludeSourceLineage { get; init; } = true;

    public int SourceLineageMaxDepth { get; init; } = 16;

    public void Validate()
    {
        if (MaxDepth < 0)
            throw new ArgumentOutOfRangeException(nameof(MaxDepth), "MaxDepth must be zero or greater.");
        if (EventsLimit is < 1 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(EventsLimit), "EventsLimit must be between 1 and 1000.");
        if (SourceLineageMaxDepth < 1)
            throw new ArgumentOutOfRangeException(nameof(SourceLineageMaxDepth));
    }
}
