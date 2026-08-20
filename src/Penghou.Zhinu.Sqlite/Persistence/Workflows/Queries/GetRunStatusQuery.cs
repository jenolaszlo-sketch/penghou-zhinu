using Microsoft.Data.Sqlite;
using System.Globalization;

namespace Penghou.Zhinu.Sqlite.Persistence.Workflows;

internal sealed class GetRunStatusQuery
{
    public async ValueTask<WorkflowStatus?> ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid workflowRunId,
        CancellationToken cancellationToken)
    {
        await using var command = SqliteStoreSupport.CreateCommand(connection, transaction, """
            SELECT status FROM workflow_runs WHERE id = $id;
            """);
        command.Parameters.AddWithValue("$id", SqliteStoreSupport.Format(workflowRunId));
        var value = await command.ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false);
        return value is null
            ? null
            : (WorkflowStatus)Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }
}
