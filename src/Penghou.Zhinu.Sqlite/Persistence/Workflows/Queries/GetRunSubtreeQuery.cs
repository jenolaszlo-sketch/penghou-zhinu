using Microsoft.Data.Sqlite;

namespace Penghou.Zhinu.Sqlite.Persistence.Workflows;

internal sealed class GetRunSubtreeQuery
{
    public async ValueTask<IReadOnlyList<WorkflowRun>> ExecuteAsync(
        SqliteConnection connection,
        Guid workflowRunId,
        int maxDepth,
        CancellationToken cancellationToken)
    {
        await using var command = SqliteStoreSupport.CreateCommand(connection, null, $"""
            WITH RECURSIVE subtree(id, depth) AS (
                SELECT id, 0 FROM workflow_runs WHERE id = $rootId
                UNION ALL
                SELECT run.id, subtree.depth + 1
                FROM workflow_runs run
                JOIN subtree ON run.parent_run_id = subtree.id
                WHERE subtree.depth < $maxDepth
            )
            SELECT {SqliteStoreSupport.RunColumns}
            FROM workflow_runs
            WHERE id IN (SELECT id FROM subtree)
            ORDER BY created_at, id;
            """);
        command.Parameters.AddWithValue("$rootId", SqliteStoreSupport.Format(workflowRunId));
        command.Parameters.AddWithValue("$maxDepth", maxDepth);
        var results = new List<WorkflowRun>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            results.Add(SqliteStoreSupport.ReadRun(reader));
        return results;
    }
}
