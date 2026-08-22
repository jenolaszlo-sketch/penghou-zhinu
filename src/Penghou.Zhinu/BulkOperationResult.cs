namespace Penghou.Zhinu;

/// <summary>
/// Result of a bulk operation applied independently to many runs. Bulk
/// operations are not atomic: each item is applied separately and the operation
/// may partially succeed. <see cref="Failed"/> lists items that could not be
/// applied together with the reason.
/// </summary>
public sealed record BulkOperationResult
{
    public required int Succeeded { get; init; }
    public required IReadOnlyList<BulkOperationFailure> Failed { get; init; }

    public int FailedCount => Failed.Count;
    public bool AllSucceeded => FailedCount == 0;
}

/// <summary>A single failed item in a bulk operation.</summary>
public sealed record BulkOperationFailure
{
    public required Guid ItemId { get; init; }
    public required string Error { get; init; }
}
