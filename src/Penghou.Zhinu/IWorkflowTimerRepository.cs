namespace Penghou.Zhinu;

/// <summary>
/// Persists delayed step scheduling (timers): a step becomes runnable again at
/// a future <c>available_at</c> timestamp.
/// </summary>
public interface IWorkflowTimerRepository
{
    ValueTask ScheduleDelayAsync(
        Guid stepId,
        string ownerId,
        DateTimeOffset availableAt,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    ValueTask CompleteDelayAsync(
        Guid stepId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);
}
