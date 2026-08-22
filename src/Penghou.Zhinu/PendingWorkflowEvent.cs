namespace Penghou.Zhinu;

/// <summary>A workflow event buffered during a step execution and committed atomically with the step.</summary>
public sealed record PendingWorkflowEvent(
    string EventType,
    string? DataJson);
