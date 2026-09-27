using Microsoft.Data.Sqlite;
using System.Globalization;

namespace Penghou.Zhinu.Sqlite.Persistence.Steps;

/// <summary>
/// Reads the owning generation status bound to a run, if any. Prefers an
/// owning (active or quiescing) generation so in-flight admission
/// (created/prepared) never fences a starting run; falls back to the latest
/// binding otherwise. Unbound (legacy) runs report no status and keep
/// historical claim behavior.
/// </summary>
internal sealed class GetBoundGenerationStatusQuery
{
    public async ValueTask<int?> ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid workflowRunId,
        CancellationToken cancellationToken)
    {
        await using var command = SqliteStoreSupport.CreateCommand(connection, transaction, """
            SELECT status FROM workflow_generations
            WHERE workflow_run_id = $run
            -- Prefer an owning generation: 2 is Active, 5 is Quiescing.
            ORDER BY CASE WHEN status IN (2, 5) THEN 0 ELSE 1 END, ordinal DESC
            LIMIT 1;
            """);
        command.Parameters.AddWithValue("$run", SqliteStoreSupport.Format(workflowRunId));
        var value = await command.ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false);
        return value is null or DBNull
            ? null
            : Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }
}
