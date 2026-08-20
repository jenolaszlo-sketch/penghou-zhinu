using Microsoft.Data.Sqlite;
using System.Globalization;

namespace Penghou.Zhinu.Sqlite.Persistence.Workflows;

internal sealed class GetRunsQuery
{
    public async ValueTask<IReadOnlyList<WorkflowRun>> ExecuteAsync(
        SqliteConnection connection,
        RunQuery query,
        CancellationToken cancellationToken)
    {
        var where = new List<string>();
        var command = SqliteStoreSupport.CreateCommand(connection, null, "");
        if (query.Statuses is { Count: > 0 })
        {
            var statusValues = query.Statuses
                .Select(status => ((int)status).ToString(CultureInfo.InvariantCulture))
                .ToArray();
            where.Add($"status IN ({string.Join(", ", statusValues)})");
        }
        if (!string.IsNullOrWhiteSpace(query.WorkflowName))
        {
            where.Add("workflow_name = $name");
            command.Parameters.AddWithValue("$name", query.WorkflowName);
        }
        if (!string.IsNullOrWhiteSpace(query.WorkflowVersion))
        {
            where.Add("workflow_version = $version");
            command.Parameters.AddWithValue("$version", query.WorkflowVersion);
        }
        if (query.CreatedAfter is not null)
        {
            where.Add("created_at >= $createdAfter");
            command.Parameters.AddWithValue(
                "$createdAfter",
                SqliteStoreSupport.FormatTimestamp(query.CreatedAfter.Value));
        }
        if (query.CreatedBefore is not null)
        {
            where.Add("created_at <= $createdBefore");
            command.Parameters.AddWithValue(
                "$createdBefore",
                SqliteStoreSupport.FormatTimestamp(query.CreatedBefore.Value));
        }
        if (query.AfterId is { } afterId)
        {
            var afterCreated = await ReadCreatedAtAsync(
                connection,
                afterId,
                cancellationToken).ConfigureAwait(false);
            where.Add("(created_at, id) > ($afterCreated, $afterId)");
            command.Parameters.AddWithValue("$afterCreated", afterCreated);
            command.Parameters.AddWithValue("$afterId", SqliteStoreSupport.Format(afterId));
        }
        var whereClause = where.Count == 0 ? string.Empty : $"WHERE {string.Join(" AND ", where)}";
        command.CommandText = $"""
            SELECT {SqliteStoreSupport.RunColumns}
            FROM workflow_runs
            {whereClause}
            ORDER BY created_at, id
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", query.Limit);
        var results = new List<WorkflowRun>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            results.Add(SqliteStoreSupport.ReadRun(reader));
        return results;
    }

    private static async ValueTask<string> ReadCreatedAtAsync(
        SqliteConnection connection,
        Guid workflowRunId,
        CancellationToken cancellationToken)
    {
        await using var command = SqliteStoreSupport.CreateCommand(connection, null, """
            SELECT created_at FROM workflow_runs WHERE id = $id;
            """);
        command.Parameters.AddWithValue("$id", SqliteStoreSupport.Format(workflowRunId));
        var value = await command.ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false);
        if (value is null)
        {
            throw new KeyNotFoundException(
                $"Workflow '{workflowRunId:D}' does not exist.");
        }
        return (string)value;
    }
}
