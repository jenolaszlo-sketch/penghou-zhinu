using Microsoft.Data.Sqlite;

namespace Penghou.Zhinu.Sqlite.Persistence.Steps;

internal sealed class GetStepDependenciesQuery
{
    public async ValueTask<IReadOnlyList<StepDependency>> ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid workflowRunId,
        CancellationToken cancellationToken)
    {
        await using var command = SqliteStoreSupport.CreateCommand(connection, transaction, """
            SELECT step_key, depends_on_step_key
            FROM workflow_step_dependencies
            WHERE run_id = $runId
            ORDER BY created_at, step_key, depends_on_step_key;
            """);
        command.Parameters.AddWithValue("$runId", SqliteStoreSupport.Format(workflowRunId));
        var results = new List<StepDependency>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new StepDependency(
                reader.GetString(0),
                reader.GetString(1)));
        }
        return results;
    }
}
