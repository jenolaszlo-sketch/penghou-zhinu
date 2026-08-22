namespace Penghou.Zhinu;

/// <summary>Result of a store health probe used by readiness endpoints.</summary>
public sealed record WorkflowStoreHealth
{
    public required bool IsHealthy { get; init; }
    public string? StoreName { get; init; }
    public int? SchemaVersion { get; init; }
    public bool? WalMode { get; init; }
    public string? Detail { get; init; }
}
