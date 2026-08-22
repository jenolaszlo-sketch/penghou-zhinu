using System.Collections.Concurrent;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;

namespace Penghou.Zhinu.Benchmarks;

[MemoryDiagnoser]
public class ClaimCompleteBenchmarks : IDisposable
{
    private SqliteWorkflowStore? store;
    private Guid runId;
    private string ownerId = "bench";
    private long leaseGeneration = 1;
    private int stepCounter;

    [GlobalSetup]
    public async Task Setup()
    {
        store = CreateStore();
        await store.InitializeAsync();
        runId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await store.CreateRunAsync(new WorkflowRun
        {
            Id = runId,
            WorkflowName = "bench",
            WorkflowVersion = "1",
            Status = WorkflowStatus.Pending,
            CreatedAt = now,
            UpdatedAt = now
        });
        leaseGeneration = (await store.TryClaimRunAsync(runId, ownerId, now, now + TimeSpan.FromSeconds(30)))!.Value;
    }

    [Benchmark]
    public async Task ClaimThenComplete()
    {
        var key = $"step-{Interlocked.Increment(ref stepCounter)}";
        var now = DateTimeOffset.UtcNow;
        var claim = await store!.ClaimStepAsync(new StepClaimRequest
        {
            WorkflowRunId = runId,
            StepKey = key,
            InputJson = "\"x\"",
            InputType = "System.String",
            InputHash = "h",
            OutputType = "System.String",
            OwnerId = ownerId,
            Now = now,
            LeaseExpiresAt = now + TimeSpan.FromSeconds(30),
            LeaseGeneration = leaseGeneration
        });
        if (claim.Disposition != StepClaimDisposition.Acquired)
            throw new InvalidOperationException(claim.Disposition.ToString());
        await store!.CompleteStepAsync(claim.Step.Id, ownerId, "\"result\"", DateTimeOffset.UtcNow);
    }

    public void Dispose() => Cleanup(CreateStore());
    private static void Cleanup(SqliteWorkflowStore _) { }
    private static SqliteWorkflowStore CreateStore() =>
        new(new ZhinuSqliteOptions
        {
            DatabasePath = Path.Combine(Path.GetTempPath(), "zhinu-bench", Guid.NewGuid().ToString("N"), "zhinu.db"),
            Pooling = false
        });
}

[MemoryDiagnoser]
public class CompleteResultSizeBenchmarks : IDisposable
{
    private SqliteWorkflowStore? store;
    private Guid runId;
    private string ownerId = "bench";
    private long leaseGeneration;
    private int stepCounter;

    [Params(64, 1024, 16384)]
    public int ResultBytes { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        store = CreateStore();
        await store.InitializeAsync();
        runId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await store.CreateRunAsync(new WorkflowRun
        {
            Id = runId, WorkflowName = "bench", WorkflowVersion = "1",
            Status = WorkflowStatus.Pending, CreatedAt = now, UpdatedAt = now
        });
        leaseGeneration = (await store.TryClaimRunAsync(runId, ownerId, now, now + TimeSpan.FromSeconds(30)))!.Value;
    }

    [Benchmark]
    public async Task CompleteWithResult()
    {
        var key = $"step-{Interlocked.Increment(ref stepCounter)}";
        var now = DateTimeOffset.UtcNow;
        var payload = new string('x', ResultBytes);
        var claim = await store!.ClaimStepAsync(new StepClaimRequest
        {
            WorkflowRunId = runId, StepKey = key, InputJson = "\"x\"",
            InputType = "System.String", InputHash = "h", OutputType = "System.String",
            OwnerId = ownerId, Now = now, LeaseExpiresAt = now + TimeSpan.FromSeconds(30),
            LeaseGeneration = leaseGeneration
        });
        await store!.CompleteStepAsync(claim.Step.Id, ownerId, JsonSerializer.Serialize(payload), DateTimeOffset.UtcNow);
    }

    public void Dispose() { }
    private static SqliteWorkflowStore CreateStore() =>
        new(new ZhinuSqliteOptions
        {
            DatabasePath = Path.Combine(Path.GetTempPath(), "zhinu-bench", Guid.NewGuid().ToString("N"), "zhinu.db"),
            Pooling = false
        });
}

[MemoryDiagnoser]
public class FanOutBenchmarks : IDisposable
{
    private WorkflowEngine? engine;
    private SqliteWorkflowStore? store;
    private Guid runId;

    [Params(10, 100, 1000)]
    public int Items { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        store = CreateStore();
        var registry = new WorkflowRegistry().Register("fanout", "1", new FanOutWorkflow());
        engine = new WorkflowEngine(store, registry, new ZhinuOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(5),
            LeaseDuration = TimeSpan.FromSeconds(30),
            LeaseRenewalInterval = TimeSpan.FromSeconds(10)
        });
    }

    [GlobalCleanup]
    public async Task Cleanup() => await engine!.DisposeAsync();

    [Benchmark]
    public async Task FanOutWorkflowRun()
    {
        runId = await engine!.StartAsync("fanout", "1", string.Join(',', Enumerable.Range(0, Items).Select(i => $"v{i}")));
        await engine.ExecuteAsync(runId);
        await engine.WaitForCompletionAsync<string>(runId);
    }

    public void Dispose() { }
    private static SqliteWorkflowStore CreateStore() =>
        new(new ZhinuSqliteOptions
        {
            DatabasePath = Path.Combine(Path.GetTempPath(), "zhinu-bench", Guid.NewGuid().ToString("N"), "zhinu.db"),
            Pooling = false
        });

    private sealed class FanOutWorkflow : IWorkflow<string, string>
    {
        public async Task<string> RunAsync(WorkflowContext ctx, string input, CancellationToken ct)
        {
            var results = await ctx.FanOutAsync<string, string>(
                "item", input.Split(','),
                (value, _, _) => Task.FromResult(value.ToUpperInvariant()),
                cancellationToken: ct);
            return string.Join(':', results);
        }
    }
}

[MemoryDiagnoser]
public class ArtifactPublishBenchmarks : IDisposable
{
    private SqliteWorkflowStore? store;
    private Guid runId;

    [GlobalSetup]
    public async Task Setup()
    {
        store = CreateStore();
        await store.InitializeAsync();
        runId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await store.CreateRunAsync(new WorkflowRun
        {
            Id = runId, WorkflowName = "bench", WorkflowVersion = "1",
            Status = WorkflowStatus.Pending, CreatedAt = now, UpdatedAt = now
        });
    }

    [Benchmark]
    public async Task PublishArtifact()
    {
        await store!.PublishArtifactAsync(new ArtifactPublicationRequest
        {
            WorkflowRunId = runId,
            Now = DateTimeOffset.UtcNow,
            Artifact = new WorkflowArtifactDescriptor
            {
                Name = "artifact",
                ArtifactType = "application/octet-stream",
                Location = "file:///tmp/artifact.bin",
                ContentHash = "sha256:abcdef"
            }
        });
    }

    public void Dispose() { }
    private static SqliteWorkflowStore CreateStore() =>
        new(new ZhinuSqliteOptions
        {
            DatabasePath = Path.Combine(Path.GetTempPath(), "zhinu-bench", Guid.NewGuid().ToString("N"), "zhinu.db"),
            Pooling = false
        });
}

[MemoryDiagnoser]
public class LeaseRecoveryBenchmarks : IDisposable
{
    private SqliteWorkflowStore? store;

    [Params(100, 1000, 10000)]
    public int Expired { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        store = CreateStore();
        await store.InitializeAsync();
        var now = DateTimeOffset.UtcNow;
        var expired = now - TimeSpan.FromMinutes(5);
        for (var i = 0; i < Expired; i++)
        {
            var runId = Guid.NewGuid();
            await store.CreateRunAsync(new WorkflowRun
            {
                Id = runId, WorkflowName = "bench", WorkflowVersion = "1",
                Status = WorkflowStatus.Running, CreatedAt = now, UpdatedAt = now,
                LeaseOwner = "dead", LeaseExpiresAt = expired
            });
            var claim = await store.ClaimStepAsync(new StepClaimRequest
            {
                WorkflowRunId = runId, StepKey = "s", InputJson = "\"x\"",
                InputType = "System.String", InputHash = "h", OutputType = "System.String",
                OwnerId = "dead", Now = now, LeaseExpiresAt = expired, LeaseGeneration = 1
            });
            await store.FailStepAsync(claim.Step.Id, "dead",
                new WorkflowError { Type = "x", Message = "dead", Timestamp = now },
                retryAt: null, now: now);
        }
    }

    [Benchmark]
    public async Task RecoverExpiredLeases() =>
        await store!.RecoverExpiredLeasesAsync(DateTimeOffset.UtcNow);

    public void Dispose() { }
    private static SqliteWorkflowStore CreateStore() =>
        new(new ZhinuSqliteOptions
        {
            DatabasePath = Path.Combine(Path.GetTempPath(), "zhinu-bench", Guid.NewGuid().ToString("N"), "zhinu.db"),
            Pooling = false
        });
}

[MemoryDiagnoser]
public class HistoryGrowthBenchmarks : IDisposable
{
    private SqliteWorkflowStore? store;
    private Guid runId;

    [Params(10_000, 1_000_000)]
    public int Events { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        store = CreateStore();
        await store.InitializeAsync();
        runId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await store.CreateRunAsync(new WorkflowRun
        {
            Id = runId, WorkflowName = "bench", WorkflowVersion = "1",
            Status = WorkflowStatus.Pending, CreatedAt = now, UpdatedAt = now
        });
        for (var i = 0; i < Events; i++)
            await store.AppendEventAsync(runId, "progress", null);
    }

    [Benchmark]
    public async Task ReadEventPage() =>
        await store!.GetEventsAsync(runId, afterSequence: 0, limit: 100);

    public void Dispose() { }
    private static SqliteWorkflowStore CreateStore() =>
        new(new ZhinuSqliteOptions
        {
            DatabasePath = Path.Combine(Path.GetTempPath(), "zhinu-bench", Guid.NewGuid().ToString("N"), "zhinu.db"),
            Pooling = false
        });
}
