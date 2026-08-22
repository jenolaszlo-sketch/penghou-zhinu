using FluentAssertions;
using Microsoft.Data.Sqlite;

namespace Penghou.Zhinu.Sqlite.Tests;

public abstract class WorkflowEngineTestBase : IDisposable
{
    protected readonly string root = Path.Combine(
        Path.GetTempPath(),
        "penghou-zhinu-tests",
        Guid.NewGuid().ToString("N"));

    protected sealed class RecordingPublisher : IWorkflowEventPublisher
    {
        public List<WorkflowEvent> Events { get; } = new();

        public Task PublishAsync(
            WorkflowEvent @event,
            CancellationToken cancellationToken = default)
        {
            Events.Add(@event);
            return Task.CompletedTask;
        }
    }

    protected WorkflowEngine CreateEngine<TWorkflow>(
        TWorkflow workflow,
        string name,
        TimeSpan? leaseDuration = null)
        where TWorkflow : class, IWorkflow<string, string> =>
        CreateEngine(
            new WorkflowRegistry().Register(name, "1", workflow),
            leaseDuration);

    protected WorkflowEngine CreateEngine(
        WorkflowRegistry registry,
        TimeSpan? leaseDuration = null)
    {
        var duration = leaseDuration ?? TimeSpan.FromSeconds(2);
        return new WorkflowEngine(
            CreateStore(),
            registry,
            new ZhinuOptions
            {
                LeaseDuration = duration,
                LeaseRenewalInterval = TimeSpan.FromTicks(duration.Ticks / 3),
                PollInterval = TimeSpan.FromMilliseconds(10)
            });
    }

    protected static async Task WaitUntilAsync(
        Func<Task<bool>> condition,
        CancellationToken cancellationToken)
    {
        while (!await condition().ConfigureAwait(false))
        {
            await Task.Delay(25, cancellationToken).ConfigureAwait(false);
        }
    }

    protected static async Task<bool> HasStepStatusAsync(
        WorkflowEngine engine,
        Guid runId,
        string stepKey,
        StepStatus status,
        CancellationToken cancellationToken)
    {
        var steps = await engine.GetStepsAsync(runId, cancellationToken);
        return steps.Any(item => item.StepKey == stepKey && item.Status == status);
    }

    protected SqliteWorkflowStore CreateStore() =>
        new(new ZhinuSqliteOptions
        {
            DatabasePath = Path.Combine(root, "zhinu.db"),
            BusyTimeout = TimeSpan.FromSeconds(2),
            Pooling = false
        });

    protected static async Task<Guid> CreateRunAsync(
        SqliteWorkflowStore store,
        string workflowName,
        DateTimeOffset createdAt,
        Guid? parentRunId,
        CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        await store.CreateRunAsync(
            new WorkflowRun
            {
                Id = id,
                WorkflowName = workflowName,
                WorkflowVersion = "1",
                Status = WorkflowStatus.Pending,
                CreatedAt = createdAt,
                UpdatedAt = createdAt,
                ParentRunId = parentRunId
            },
            cancellationToken);
        return id;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        DeleteTestDirectory(root);
    }

    private static void DeleteTestDirectory(string path)
    {
        for (var attempt = 1; Directory.Exists(path); attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 10)
            {
                SqliteConnection.ClearAllPools();
                GC.Collect();
                GC.WaitForPendingFinalizers();
                Thread.Sleep(50 * attempt);
            }
        }
    }
}
