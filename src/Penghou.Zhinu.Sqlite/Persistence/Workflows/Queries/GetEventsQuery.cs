using Microsoft.Data.Sqlite;

namespace Penghou.Zhinu.Sqlite.Persistence.Workflows;

internal sealed class GetEventsQuery
{
    public async ValueTask<IReadOnlyList<WorkflowEvent>> ExecuteAsync(
        SqliteConnection connection,
        Guid workflowRunId,
        long afterSequence,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var command = SqliteStoreSupport.CreateCommand(connection, null, """
            SELECT sequence, workflow_run_id, step_key, event_type,
                   timestamp, attempt, data_json
            FROM workflow_events
            WHERE workflow_run_id = $runId AND sequence > $after
            ORDER BY sequence
            LIMIT $limit;
            """);
        command.Parameters.AddWithValue("$runId", SqliteStoreSupport.Format(workflowRunId));
        command.Parameters.AddWithValue("$after", afterSequence);
        command.Parameters.AddWithValue("$limit", limit);
        var results = new List<WorkflowEvent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new WorkflowEvent
            {
                Sequence = reader.GetInt64(0),
                WorkflowRunId = Guid.Parse(reader.GetString(1)),
                StepKey = SqliteStoreSupport.GetNullableString(reader, 2),
                EventType = reader.GetString(3),
                Timestamp = SqliteStoreSupport.ParseTimestamp(reader.GetString(4)),
                Attempt = reader.IsDBNull(5) ? null : reader.GetInt32(5),
                DataJson = SqliteStoreSupport.GetNullableString(reader, 6)
            });
        }
        return results;
    }
}
