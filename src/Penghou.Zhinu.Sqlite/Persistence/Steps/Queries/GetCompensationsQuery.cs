using Microsoft.Data.Sqlite;

namespace Penghou.Zhinu.Sqlite.Persistence.Steps;

internal sealed class GetCompensationsQuery
{
    public async ValueTask<IReadOnlyList<WorkflowStepCompensation>> ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid workflowRunId,
        CancellationToken cancellationToken)
    {
        await using var command = SqliteStoreSupport.CreateCommand(connection, transaction, $"""
            SELECT {SqliteStoreSupport.CompensationColumns}
            FROM workflow_step_compensations
            WHERE workflow_run_id = $runId
            ORDER BY created_at, step_key, revision;
            """);
        command.Parameters.AddWithValue("$runId", SqliteStoreSupport.Format(workflowRunId));
        var results = new List<WorkflowStepCompensation>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            results.Add(SqliteStoreSupport.ReadCompensation(reader));
        return results;
    }
}
