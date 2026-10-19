using System.Reflection;
using BenchmarkDotNet.Attributes;
using Penghou.Zhinu.Sqlite;

namespace Penghou.Zhinu.Benchmarks;

/// <summary>One complete group-state sweep over a real executed SQLite fixture.
/// The in-memory snapshot provider isolates projection cost from database stamp I/O.</summary>
[MemoryDiagnoser]
public class ActivityGroupStateReadBenchmarks
{
    private WorkflowGroupReader reader = null!;
    private WorkflowGroupSnapshot snapshot = null!;
    private WorkflowGroupSnapshotData data = null!;

    [Params(32, 256, 1024)]
    public int Groups { get; set; }

    [GlobalSetup]
    public void Setup() => SetupAsync().GetAwaiter().GetResult();

    private async Task SetupAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "zhinu-group-state-bench", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var store = new SqliteWorkflowStore(new ZhinuSqliteOptions
            {
                DatabasePath = Path.Combine(directory, "zhinu.db"),
                Pooling = false
            });
            var manifest = ActivityGroupManifest.Create(
                new[] { new ActivityGroupDefinition("root", ActivityGroupKind.Logical, "Root") }
                    .Concat(Enumerable.Range(0, Groups).Select(index => new ActivityGroupDefinition(
                        Key(index), ActivityGroupKind.Logical, "Item", "root", index))));
            var registry = new WorkflowRegistry().Register(new WorkflowRegistration<int, int>(
                new WorkflowDefinition { Name = "group-state-benchmark", Version = "1" },
                () => new SweepWorkflow(), manifest));
            await using (var engine = new WorkflowEngine(store, registry))
            {
                var runId = await engine.StartAsync("group-state-benchmark", "1", Groups);
                await engine.ExecuteAsync(runId);
                await engine.WaitForCompletionAsync<int>(runId);
                var captured = await store.ReadGroupSnapshotAsync(runId, new WorkflowGroupReadLimits());
                data = captured.Value ?? throw new InvalidOperationException(captured.Status.ToString());
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
        var storeProxy = DispatchProxy.Create<ISnapshotStore, SnapshotStore>();
        ((SnapshotStore)(object)storeProxy).Data = data;
        reader = new WorkflowGroupReader(storeProxy, new WorkflowGroupQueryOptions { SnapshotLifetime = TimeSpan.FromHours(1) });
        snapshot = (await reader.GetRunGroupSnapshotAsync(data.Run.RunId)).Value!;
        if (await ReadWholeTree() != 2 * Groups) throw new InvalidOperationException("Unexpected aggregate counts.");
    }

    [Benchmark]
    public async Task<int> ReadWholeTree()
    {
        var count = 0;
        foreach (var occurrence in data.Occurrences)
        {
            var result = await reader.GetGroupStateAsync(data.Run.RunId, snapshot.SnapshotToken, occurrence.OccurrenceId);
            if (result.Value?.State.Status != StepStatus.Completed) throw new InvalidOperationException(result.Status.ToString());
            count += result.Value.State.Work.Known;
        }
        return count;
    }

    [GlobalCleanup]
    public void Cleanup() => reader?.Dispose();

    private static string Key(int index) => $"item/{index:D4}";

    private sealed class SweepWorkflow : IWorkflow<int, int>
    {
        public async Task<int> RunAsync(WorkflowContext context, int input, CancellationToken cancellationToken)
        {
            var parent = context.GetActivityGroup("root");
            for (var index = 0; index < input; index++)
            {
                var key = Key(index);
                var occurrence = context.GetActivityGroup(key, parent);
                await context.StepAsync(key, index, (value, _) => Task.FromResult(value),
                    new StepOptions { Group = new ActivityGroupStepBinding(new ActivityGroupMembership(key), occurrence) }, cancellationToken);
            }
            return input;
        }
    }

    public interface ISnapshotStore : IWorkflowStore, IWorkflowGroupSnapshotRepository { }

    public class SnapshotStore : DispatchProxy
    {
        public WorkflowGroupSnapshotData Data { get; set; } = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name switch
        {
            nameof(IWorkflowGroupSnapshotRepository.ReadGroupSnapshotAsync) =>
                new ValueTask<WorkflowGroupQueryResult<WorkflowGroupSnapshotData>>(
                    new WorkflowGroupQueryResult<WorkflowGroupSnapshotData>(WorkflowGroupReadStatus.Success, Data)),
            nameof(IWorkflowGroupSnapshotRepository.GetGroupSnapshotRunStampAsync) => new ValueTask<long?>(Data.RunCreatedSequence),
            _ => throw new NotSupportedException(targetMethod?.Name)
        };
    }
}
