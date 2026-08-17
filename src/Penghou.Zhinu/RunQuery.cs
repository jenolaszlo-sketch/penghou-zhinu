namespace Penghou.Zhinu;

/// <summary>
/// Defines a filtered, cursor-paginated query over persisted workflow runs.
/// Cursor pagination is stable while new runs are inserted: pass the last run's
/// <c>Id</c> as <see cref="AfterId"/> to fetch the next page in creation order.
/// </summary>
public sealed record RunQuery
{
    /// <summary>Only runs with these statuses are returned. Null means all statuses.</summary>
    public IReadOnlyList<WorkflowStatus>? Statuses { get; init; }

    /// <summary>Only runs matching this workflow name are returned. Null means all names.</summary>
    public string? WorkflowName { get; init; }

    /// <summary>Only runs matching this workflow version are returned. Null means all versions.</summary>
    public string? WorkflowVersion { get; init; }

    /// <summary>Only runs created at or after this time are returned.</summary>
    public DateTimeOffset? CreatedAfter { get; init; }

    /// <summary>Only runs created at or before this time are returned.</summary>
    public DateTimeOffset? CreatedBefore { get; init; }

    /// <summary>
    /// The <see cref="WorkflowRun.Id"/> of the last run of the previous page.
    /// Results continue in ascending creation order strictly after this run.
    /// </summary>
    public Guid? AfterId { get; init; }

    /// <summary>Maximum number of runs to return. Defaults to 100.</summary>
    public int Limit { get; init; } = 100;

    /// <summary>Validates the query and throws on invalid combinations.</summary>
    public void Validate()
    {
        if (Limit < 1)
            throw new ArgumentOutOfRangeException(nameof(Limit));
        if (Limit > 1000)
            throw new ArgumentOutOfRangeException(nameof(Limit));
    }
}
