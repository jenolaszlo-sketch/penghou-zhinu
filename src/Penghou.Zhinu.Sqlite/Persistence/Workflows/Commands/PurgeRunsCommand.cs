using Microsoft.Data.Sqlite;
using System.Globalization;

namespace Penghou.Zhinu.Sqlite.Persistence.Workflows;

internal sealed class PurgeRunsCommand
{
    public async ValueTask<int> ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateTimeOffset olderThan,
        IReadOnlyList<WorkflowStatus>? statuses,
        CancellationToken cancellationToken)
    {
        var statusClause = string.Empty;
        if (statuses is { Count: > 0 })
        {
            var values = string.Join(
                ", ",
                statuses.Select(status =>
                    ((int)status).ToString(CultureInfo.InvariantCulture)));
            statusClause = $" AND status IN ({values})";
        }
        await using var command = SqliteStoreSupport.CreateCommand(connection, transaction, $"""
            DELETE FROM workflow_runs
            WHERE created_at < $olderThan{statusClause};
            """);
        command.Parameters.AddWithValue("$olderThan", SqliteStoreSupport.FormatTimestamp(olderThan));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
