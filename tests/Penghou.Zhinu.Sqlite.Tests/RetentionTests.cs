using FluentAssertions;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;

namespace Penghou.Zhinu.Sqlite.Tests;

/// <summary>
/// Terminal-run retention: completion-time cutoff, terminal-only default
/// with explicit active opt-in, preview agreement, restart-race safety,
/// run-scoped cascading with independent instances/children, and bounded
/// batches. The legacy creation-time purge behavior is untouched. Old and
/// fresh runs share one database file across two clock epochs.
/// </summary>
public sealed class RetentionTests : WorkflowEngineTestBase
{
    private static readonly DateTimeOffset Past =
        new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task TerminalCutoff_PurgesOnlyOldTerminal()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = DatabasePath();
        var oldCompleted = await CompleteOnOldDatabaseAsync(path, "x", ct);
        var oldPending = await StartOnOldDatabaseAsync(path, "y", ct);
        var now = CurrentEngine(path, out var nowStore);
        var freshCompleted = await CompleteAsync(now, "x", ct);
        var options = new RunRetentionOptions { OlderThan = DateTimeOffset.UtcNow.AddDays(-7) };

        var preview = await nowStore.PreviewRetentionAsync(options, ct);
        preview.EligibleRunCount.Should().Be(1);
        preview.SampleRunIds.Should().Equal(oldCompleted);

        (await nowStore.PurgeRetainedRunsAsync(options, ct)).Should().Be(1);
        (await nowStore.GetRunAsync(oldCompleted, ct)).Should().BeNull();
        (await nowStore.GetRunAsync(oldPending, ct)).Should().NotBeNull();
        (await nowStore.GetRunAsync(freshCompleted, ct)).Should().NotBeNull();
        await now.DisposeAsync();
    }

    [Fact]
    public async Task ActiveRuns_RequireExplicitOptIn()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = DatabasePath();
        var pending = await StartOnOldDatabaseAsync(path, "x", ct);
        var now = CurrentEngine(path, out var nowStore);
        var standard = new RunRetentionOptions { OlderThan = DateTimeOffset.UtcNow.AddDays(-7) };

        (await nowStore.PurgeRetainedRunsAsync(standard, ct)).Should().Be(0);
        var gated = () => nowStore.PurgeRetainedRunsAsync(
            standard with { Statuses = [WorkflowStatus.Pending] }, ct).AsTask();
        await gated.Should().ThrowAsync<ArgumentException>();
        var explicitOptions = standard with
        {
            Statuses = [WorkflowStatus.Pending],
            IncludeActiveRuns = true
        };
        (await nowStore.PreviewRetentionAsync(explicitOptions, ct)).EligibleRunCount.Should().Be(1);
        (await nowStore.PurgeRetainedRunsAsync(explicitOptions, ct)).Should().Be(1);
        (await nowStore.GetRunAsync(pending, ct)).Should().BeNull();
        await now.DisposeAsync();
    }

    [Fact]
    public async Task RestartRace_PreviewIsNotALock()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = DatabasePath();
        var runId = await CompleteOnOldDatabaseAsync(path, "x", ct);
        var now = CurrentEngine(path, out var nowStore);
        var options = new RunRetentionOptions { OlderThan = DateTimeOffset.UtcNow.AddDays(-7) };
        (await nowStore.PreviewRetentionAsync(options, ct)).EligibleRunCount.Should().Be(1);

        await now.RestartStepAsync(runId, "first", ct);

        (await nowStore.PurgeRetainedRunsAsync(options, ct)).Should().Be(0);
        (await nowStore.GetRunAsync(runId, ct))!.Status.Should().Be(WorkflowStatus.Pending);
        await now.DisposeAsync();
    }

    [Fact]
    public async Task Purge_CascadesRunScopeKeepsInstance()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = DatabasePath();
        var runId = await CompleteOnOldDatabaseAsync(path, "x", ct);
        var now = CurrentEngine(path, out var nowStore);
        var generation = (await nowStore.GetGenerationByRunAsync(runId, ct))!;
        await nowStore.PublishArtifactAsync(
            new ArtifactPublicationRequest
            {
                WorkflowRunId = runId,
                Artifact = new WorkflowArtifactDescriptor
                {
                    Name = "note",
                    ArtifactType = "text/plain",
                    Location = "file:///note"
                },
                Now = DateTimeOffset.UtcNow
            },
            ct);

        (await nowStore.PurgeRetainedRunsAsync(
            new RunRetentionOptions { OlderThan = DateTimeOffset.UtcNow.AddDays(-7) }, ct))
            .Should().Be(1);

        (await nowStore.GetRunAsync(runId, ct)).Should().BeNull();
        (await nowStore.GetStepsAsync(runId, ct)).Should().BeEmpty();
        (await nowStore.GetArtifactsAsync(runId, ct)).Should().BeEmpty();
        (await nowStore.GetGenerationByRunAsync(runId, ct)).Should().BeNull();
        (await nowStore.GetInstanceAsync(generation.InstanceId, ct)).Should().NotBeNull();
        await now.DisposeAsync();
    }

    [Fact]
    public async Task ChildRun_RetainedIndependentlyOfParent()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = DatabasePath();
        var parentId = await CompleteOnOldDatabaseAsync(path, "x", ct);
        var now = CurrentEngine(path, out var nowStore);
        var childId = Guid.NewGuid();
        await nowStore.CreateRunAsync(
            new WorkflowRun
            {
                Id = childId,
                WorkflowName = "retain",
                WorkflowVersion = "1",
                Status = WorkflowStatus.Pending,
                CreatedAt = Past,
                UpdatedAt = Past,
                ParentRunId = parentId
            },
            ct);

        (await nowStore.PurgeRetainedRunsAsync(
            new RunRetentionOptions { OlderThan = DateTimeOffset.UtcNow.AddDays(-7) }, ct))
            .Should().Be(1);

        (await nowStore.GetRunAsync(parentId, ct)).Should().BeNull();
        (await nowStore.GetRunAsync(childId, ct)).Should().NotBeNull();
        await now.DisposeAsync();
    }

    [Fact]
    public async Task Batches_DeleteAllEligible()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = DatabasePath();
        var old = OldEngine(path, out _);
        for (var i = 0; i < 3; i++)
            await CompleteAsync(old, $"x{i}", ct);
        await old.DisposeAsync();
        var now = CurrentEngine(path, out var nowStore);
        var options = new RunRetentionOptions
        {
            OlderThan = DateTimeOffset.UtcNow.AddDays(-7),
            BatchSize = 1
        };

        (await nowStore.PreviewRetentionAsync(options with { BatchSize = 100 }, ct))
            .EligibleRunCount.Should().Be(3);
        (await nowStore.PurgeRetainedRunsAsync(options, ct)).Should().Be(3);
        (await nowStore.PreviewRetentionAsync(options, ct)).EligibleRunCount.Should().Be(0);
        await now.DisposeAsync();
    }

    private string DatabasePath() => Path.Combine(root, $"zhinu-{Guid.NewGuid():N}.db");

    private WorkflowEngine OldEngine(string path, out SqliteWorkflowStore store)
    {
        var clock = new AnchoredClock(Past);
        store = new SqliteWorkflowStore(new ZhinuSqliteOptions
        {
            DatabasePath = path,
            BusyTimeout = TimeSpan.FromSeconds(2),
            Pooling = false,
            TimeProvider = clock
        });
        return new WorkflowEngine(
            store,
            new WorkflowRegistry().Register("retain", "1", new RetainWorkflow()),
            new ZhinuOptions { PollInterval = TimeSpan.FromMilliseconds(10) },
            null,
            clock,
            null,
            null);
    }

    private WorkflowEngine CurrentEngine(string path, out SqliteWorkflowStore store)
    {
        store = new SqliteWorkflowStore(new ZhinuSqliteOptions
        {
            DatabasePath = path,
            BusyTimeout = TimeSpan.FromSeconds(2),
            Pooling = false
        });
        return new WorkflowEngine(
            store,
            new WorkflowRegistry().Register("retain", "1", new RetainWorkflow()),
            new ZhinuOptions { PollInterval = TimeSpan.FromMilliseconds(10) });
    }

    private static async Task<Guid> CompleteAsync(
        WorkflowEngine engine, string input, CancellationToken ct)
    {
        var id = await engine.StartAsync("retain", "1", input, cancellationToken: ct);
        await engine.ExecuteAsync(id, ct);
        await engine.WaitForCompletionAsync<string>(id, cancellationToken: ct);
        return id;
    }

    private async Task<Guid> CompleteOnOldDatabaseAsync(
        string path, string input, CancellationToken ct)
    {
        var old = OldEngine(path, out _);
        try
        {
            return await CompleteAsync(old, input, ct);
        }
        finally
        {
            await old.DisposeAsync();
        }
    }

    private async Task<Guid> StartOnOldDatabaseAsync(
        string path, string input, CancellationToken ct)
    {
        var old = OldEngine(path, out _);
        try
        {
            return await old.StartAsync("retain", "1", input, cancellationToken: ct);
        }
        finally
        {
            await old.DisposeAsync();
        }
    }

    private sealed class AnchoredClock(DateTimeOffset origin) : TimeProvider
    {
        private readonly DateTimeOffset startedReal = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() =>
            origin + (DateTimeOffset.UtcNow - startedReal);

        public override long GetTimestamp() => TimeProvider.System.GetTimestamp();
    }

    private sealed class RetainWorkflow : IWorkflow<string, string>
    {
        public Task<string> RunAsync(
            WorkflowContext context, string input, CancellationToken cancellationToken) =>
            context.StepAsync("first", _ => Task.FromResult(input), cancellationToken: cancellationToken);
    }
}
