using Microsoft.Data.Sqlite;
using System.Globalization;

namespace Penghou.Zhinu.Sqlite.Persistence.Workflows;

internal sealed class AppendEventCommand
{
    public async ValueTask<WorkflowEvent> ExecuteAsync(
        SqliteConnection connection,
        Guid workflowRunId,
        string eventType,
        string? dataJson,
        string? stepKey,
        int? attempt,
        DateTimeOffset timestamp,
        CancellationToken cancellationToken)
    {
        await using var command = SqliteStoreSupport.CreateCommand(connection, null, """
            INSERT INTO workflow_events
            (workflow_run_id, step_key, event_type, timestamp, attempt, data_json)
            VALUES ($runId, $stepKey, $eventType, $timestamp, $attempt, $dataJson);
            SELECT last_insert_rowid();
            """);
        command.Parameters.AddWithValue("$runId", SqliteStoreSupport.Format(workflowRunId));
        command.Parameters.AddWithValue("$stepKey", SqliteStoreSupport.DbValue(stepKey));
        command.Parameters.AddWithValue("$eventType", eventType);
        command.Parameters.AddWithValue("$timestamp", SqliteStoreSupport.FormatTimestamp(timestamp));
        command.Parameters.AddWithValue(
            "$attempt",
            attempt is null ? DBNull.Value : attempt.Value);
        command.Parameters.AddWithValue("$dataJson", SqliteStoreSupport.DbValue(dataJson));
        var sequence = Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
        return new WorkflowEvent
        {
            Sequence = sequence,
            WorkflowRunId = workflowRunId,
            StepKey = stepKey,
            EventType = eventType,
            Timestamp = timestamp,
            Attempt = attempt,
            DataJson = dataJson
        };
    }
}
