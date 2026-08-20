using Microsoft.Data.Sqlite;

namespace Penghou.Zhinu.Sqlite.Persistence.Steps;

internal sealed class GetStepByIdQuery
{
    public async ValueTask<WorkflowStepRun?> ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid stepId,
        CancellationToken cancellationToken)
    {
        await using var command = SqliteStoreSupport.CreateCommand(connection, transaction, $"""
            SELECT {SqliteStoreSupport.StepColumns}
            FROM workflow_steps
            WHERE id = $id;
            """);
        command.Parameters.AddWithValue("$id", SqliteStoreSupport.Format(stepId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? SqliteStoreSupport.ReadStep(reader)
            : null;
    }
}
