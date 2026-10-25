using Penghou.Zhinu.Sqlite.Persistence.Groups;

namespace Penghou.Zhinu.Sqlite;

public sealed partial class SqliteWorkflowStore
{
    public ValueTask<WorkflowGroupQueryResult<WorkflowGroupSnapshotData>> ReadGroupSnapshotAsync(
        Guid workflowRunId,
        WorkflowGroupReadLimits limits,
        CancellationToken cancellationToken = default) =>
        new SqliteWorkflowGroupSnapshotRepository(factory).ReadGroupSnapshotAsync(
            workflowRunId, limits, cancellationToken);

    public ValueTask<long?> GetGroupSnapshotRunStampAsync(
        Guid workflowRunId,
        CancellationToken cancellationToken = default) =>
        new SqliteWorkflowGroupSnapshotRepository(factory).GetGroupSnapshotRunStampAsync(
            workflowRunId, cancellationToken);
}
