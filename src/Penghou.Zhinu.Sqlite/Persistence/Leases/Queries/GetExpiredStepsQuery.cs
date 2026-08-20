using Microsoft.Data.Sqlite;

namespace Penghou.Zhinu.Sqlite.Persistence.Leases;

internal sealed class GetExpiredStepsQuery
{
    public async ValueTask<IReadOnlyList<WorkflowStepRun>> ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = SqliteStoreSupport.CreateCommand(connection, transaction, $"""
            SELECT {SqliteStoreSupport.StepColumns}
            FROM workflow_steps
            WHERE status = $running AND lease_expires_at <= $now;
            """);
        command.Parameters.AddWithValue("$running", (int)StepStatus.Running);
        command.Parameters.AddWithValue("$now", SqliteStoreSupport.FormatTimestamp(now));
        return await SqliteStoreSupport.ReadStepsAsync(command, cancellationToken)
            .ConfigureAwait(false);
    }
}
