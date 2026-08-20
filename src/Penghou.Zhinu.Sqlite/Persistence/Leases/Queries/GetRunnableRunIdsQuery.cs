using Microsoft.Data.Sqlite;

namespace Penghou.Zhinu.Sqlite.Persistence.Leases;

internal sealed class GetRunnableRunIdsQuery
{
    public async ValueTask<IReadOnlyList<Guid>> ExecuteAsync(
        SqliteConnection connection,
        DateTimeOffset now,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var command = SqliteStoreSupport.CreateCommand(connection, null, """
            SELECT id
            FROM workflow_runs
            WHERE status IN ($pending, $running, $rollingBack)
              AND (lease_expires_at IS NULL OR lease_expires_at <= $now)
            ORDER BY created_at, id
            LIMIT $limit;
            """);
        command.Parameters.AddWithValue("$pending", (int)WorkflowStatus.Pending);
        command.Parameters.AddWithValue("$running", (int)WorkflowStatus.Running);
        command.Parameters.AddWithValue("$rollingBack", (int)WorkflowStatus.RollingBack);
        command.Parameters.AddWithValue("$now", SqliteStoreSupport.FormatTimestamp(now));
        command.Parameters.AddWithValue("$limit", limit);
        var results = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            results.Add(Guid.Parse(reader.GetString(0)));
        return results;
    }
}
