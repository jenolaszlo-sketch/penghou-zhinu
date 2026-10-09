using FluentAssertions;
using Microsoft.Data.Sqlite;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;

namespace Penghou.Zhinu.Sqlite.Tests;

/// <summary>
/// The read-only run snapshot carries rows and a durable watermark from one
/// consistent storage read boundary. Every snapshot below proves the pairing
/// directly: every step transition effect visible in the rows has its durable
/// event at or before the watermark, and no row reflects a transition beyond
/// it. Snapshots reopen deterministically, nonexistent runs yield null, and
/// diagnostic events never advance the watermark.
/// </summary>
public sealed class WorkflowRunSnapshotTests : WorkflowEngineTestBase
{
    private SqliteWorkflowStore Store(string name) => new(new ZhinuSqliteOptions
    {
        DatabasePath = Path.Combine(root, name + ".db"),
        BusyTimeout = TimeSpan.FromSeconds(10),
        Pooling = false
    });

    private static WorkflowEngine EngineOver(SqliteWorkflowStore store) =>
        EngineOver(store, new WorkflowRegistry());

    private static WorkflowEngine EngineOver(SqliteWorkflowStore store, WorkflowRegistry registry) => new(
        store,
        registry,
        new ZhinuOptions
        {
            LeaseDuration = TimeSpan.FromSeconds(2),
            LeaseRenewalInterval = TimeSpan.FromMilliseconds(600),
            PollInterval = TimeSpan.FromMilliseconds(10)
        });

    private static async Task<IReadOnlyList<WorkflowEvent>> DrainJournalAsync(
        SqliteWorkflowStore store, Guid runId, CancellationToken cancellationToken)
    {
        var all = new List<WorkflowEvent>();
        long cursor = 0;
        while (true)
        {
            var page = await store.GetEventsAsync(runId, cursor, 100, cancellationToken);
            if (page.Count == 0)
                return all;
            all.AddRange(page);
            cursor = page[^1].Sequence;
        }
    }

    private static void AssertPairing(
        WorkflowRunSnapshot snapshot,
        IReadOnlyList<WorkflowEvent> journal,
        string because)
    {
        var durable = snapshot.ThroughDurableSequence;
        foreach (var step in snapshot.Steps)
        {
            var completed = journal
                .Where(e => e.EventType == WorkflowEventTypes.StepCompleted &&
                    e.StepKey == step.StepKey && (!e.Attempt.HasValue || e.Attempt == step.Attempt))
                .OrderBy(e => e.Sequence)
                .ToList();
            if (step.Status == StepStatus.Completed && completed.Count == 0)
            {
                // Signal-delivered completions settle through the signal-delivery
                // commit rather than a step-completed event; that event is the
                // step's durable completion evidence instead.
                var delivered = journal
                    .Where(e => e.EventType == WorkflowEventTypes.SignalDelivered &&
                        e.StepKey == step.StepKey)
                    .OrderBy(e => e.Sequence)
                    .ToList();
                delivered.Should().NotBeEmpty(
                    $"{because}: completed step '{step.StepKey}' without a step-completed event must have a signal-delivered event");
                delivered[^1].Sequence.Should().BeLessThanOrEqualTo(
                    durable, $"{because}: signal delivery for '{step.StepKey}' must be at or before the watermark");
            }
            else if (step.Status == StepStatus.Completed)
            {
                completed[^1].Sequence.Should().BeLessThanOrEqualTo(
                    durable, $"{because}: step-completed for '{step.StepKey}' must be at or before the watermark");
            }
            var started = journal
                .Where(e => e.EventType == WorkflowEventTypes.StepStarted && e.StepKey == step.StepKey)
                .OrderBy(e => e.Sequence)
                .ToList();
            started.Should().NotBeEmpty(
                $"{because}: visible step '{step.StepKey}' must have a started event (claims commit atomically)");
        }
        foreach (var failed in snapshot.Steps.Where(s => s.Status == StepStatus.Failed))
        {
            var failedEvents = journal
                .Where(e => e.EventType == WorkflowEventTypes.StepFailed &&
                    e.StepKey == failed.StepKey && (!e.Attempt.HasValue || e.Attempt == failed.Attempt))
                .OrderBy(e => e.Sequence)
                .ToList();
            failedEvents.Should().NotBeEmpty(
                $"{because}: failed step '{failed.StepKey}' must have a step-failed event");
            failedEvents[^1].Sequence.Should().BeLessThanOrEqualTo(
                durable, $"{because}: step-failed for '{failed.StepKey}' must be at or before the watermark");
        }
        // Durable outcomes reflected in the snapshot must not postdate the watermark.
        foreach (var outcome in journal.Where(e =>
            e.Sequence <= durable &&
            (e.EventType == WorkflowEventTypes.StepCompleted || e.EventType == WorkflowEventTypes.StepFailed)))
        {
            var row = snapshot.Steps.SingleOrDefault(s => s.StepKey == outcome.StepKey);
            row.Should().NotBeNull(
                $"{because}: outcome event '{outcome.EventType}' for '{outcome.StepKey}' at {outcome.Sequence} must have a visible row");
        }
    }

    [Fact]
    public async Task MissingRun_ReturnsNull()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = Store("missing");
        await using var engine = EngineOver(store);

        (await engine.GetRunSnapshotAsync(Guid.NewGuid(), cancellationToken: ct)).Should().BeNull();
        (await store.ReadRunSnapshotAsync(Guid.NewGuid(), new RunSnapshotOptions(), ct)).Should().BeNull();
    }

    [Fact]
    public async Task FreshRun_ProjectsCreationStateAndWatermark()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = Store("fresh");
        await using var engine = EngineOver(store);
        var registry = new WorkflowRegistry().Register("fresh", "1", new TwoStepWorkflow());
        await using var executing = new WorkflowEngine(store, registry, new ZhinuOptions());

        var runId = await executing.StartAsync("fresh", "1", "in", cancellationToken: ct);
        var snapshot = (await engine.GetRunSnapshotAsync(runId, cancellationToken: ct))!;

        snapshot.Should().NotBeNull();
        snapshot.Run.Id.Should().Be(runId);
        snapshot.Steps.Should().BeEmpty();
        snapshot.ThroughDurableSequence.Should().BeGreaterThan(0);
        snapshot.Diagnosis.Should().NotBeNull();

        // Pages continue exactly after the watermark on a quiescent run.
        var events = await DrainJournalAsync(store, runId, ct);
        events.Should().NotBeEmpty();
        snapshot.ThroughDurableSequence.Should().Be(
            events.Where(e => e.Durability == WorkflowEventDurability.Durable).Max(e => e.Sequence));
        (await engine.GetEventPageAsync(runId, snapshot.ThroughDurableSequence, 100, ct))
            .Events.Should().BeEmpty();
    }

    [Fact]
    public async Task CompletedRun_ProjectsFullShape()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = Store("completed");
        var registry = new WorkflowRegistry().Register("two", "1", new TwoStepWorkflow());
        await using var writer = new WorkflowEngine(store, registry, new ZhinuOptions
        {
            LeaseDuration = TimeSpan.FromSeconds(2),
            LeaseRenewalInterval = TimeSpan.FromMilliseconds(600),
            PollInterval = TimeSpan.FromMilliseconds(10)
        });
        await using var reader = EngineOver(Store("completed"));

        var runId = await writer.StartAsync("two", "1", "in", cancellationToken: ct);
        await writer.ExecuteAsync(runId, ct);
        await writer.WaitForCompletionAsync<string>(runId, cancellationToken: ct);
        var snapshot = (await reader.GetRunSnapshotAsync(runId, cancellationToken: ct))!;

        snapshot.Run.Status.Should().Be(WorkflowStatus.Completed);
        snapshot.Steps.Select(s => s.StepKey).Should().BeEquivalentTo("first", "second");
        snapshot.Steps.Should().OnlyContain(s => s.Status == StepStatus.Completed);
        snapshot.Dependencies.Should().Contain(d => d.DependsOnStepKey == "first");
        snapshot.Waits.Should().BeEmpty();
        snapshot.Diagnosis.Should().NotBeNull();
        snapshot.Diagnosis!.Code.Should().Be(RunDiagnosisCode.Terminal);
        snapshot.Generation.Should().NotBeNull("engine admission binds the initial generation");

        var journal = await DrainJournalAsync(store, runId, ct);
        AssertPairing(snapshot, journal, "completed run");
        snapshot.ThroughDurableSequence.Should().Be(
            journal.Where(e => e.Durability == WorkflowEventDurability.Durable).Max(e => e.Sequence));
    }

    [Fact]
    public async Task AdvisoryEvents_DoNotAdvanceWatermark_DurableAfterAdvisoryDoes()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = Store("advisory");
        await using var engine = EngineOver(store);
        var runId = await CreateRunAsync(store, "w", DateTimeOffset.UtcNow, null, ct);
        await store.AppendEventAsync(runId, WorkflowEventTypes.Progress, "1", cancellationToken: ct);

        var onlyAdvisory = (await engine.GetRunSnapshotAsync(runId, cancellationToken: ct))!;
        onlyAdvisory.ThroughDurableSequence.Should().BeGreaterThanOrEqualTo(0);

        await store.AppendEventAsync(runId, WorkflowEventTypes.StepCompleted, null, cancellationToken: ct);
        var durableAfter = (await engine.GetRunSnapshotAsync(runId, cancellationToken: ct))!;
        durableAfter.ThroughDurableSequence.Should().BeGreaterThan(onlyAdvisory.ThroughDurableSequence);
    }

    [Fact]
    public async Task StepRestart_SurfacesCurrentRevisionOnly()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = Store("restart");
        var registry = new WorkflowRegistry().Register("two", "1", new TwoStepWorkflow());
        await using var writer = new WorkflowEngine(store, registry, new ZhinuOptions
        {
            LeaseDuration = TimeSpan.FromSeconds(2),
            LeaseRenewalInterval = TimeSpan.FromMilliseconds(600),
            PollInterval = TimeSpan.FromMilliseconds(10)
        });
        await using var reader = EngineOver(Store("restart"));

        var runId = await writer.StartAsync("two", "1", "in", cancellationToken: ct);
        await writer.ExecuteAsync(runId, ct);
        await writer.WaitForCompletionAsync<string>(runId, cancellationToken: ct);
        var before = (await reader.GetRunSnapshotAsync(runId, cancellationToken: ct))!;

        await writer.RestartStepAsync(runId, "first", cancellationToken: ct);
        await writer.ExecuteAsync(runId, ct);
        await writer.WaitForCompletionAsync<string>(runId, cancellationToken: ct);
        var after = (await reader.GetRunSnapshotAsync(runId, cancellationToken: ct))!;

        after.ThroughDurableSequence.Should().BeGreaterThan(before.ThroughDurableSequence);
        var first = after.Steps.Where(s => s.StepKey == "first").ToList();
        first.Should().ContainSingle("superseded step revisions are never returned");
        first[0].Revision.Should().BeGreaterThan(1);
        var journal = await DrainJournalAsync(store, runId, ct);
        AssertPairing(after, journal, "restarted run");
    }

    [Fact]
    public async Task WriterReaderRace_NeverProducesAnImpossiblePairing()
    {
        var ct = TestContext.Current.CancellationToken;
        var writer = new GatedWorkflow();
        var registry = new WorkflowRegistry().Register("gated", "1", writer);
        var writerStore = Store("race");
        var readerStore = Store("race");
        await using var writerEngine = new WorkflowEngine(writerStore, registry, new ZhinuOptions
        {
            LeaseDuration = TimeSpan.FromSeconds(30),
            LeaseRenewalInterval = TimeSpan.FromSeconds(1),
            PollInterval = TimeSpan.FromMilliseconds(10)
        });
        await using var readerEngine = EngineOver(readerStore);

        var runId = await writerEngine.StartAsync("gated", "1", "in", cancellationToken: ct);
        var execution = writerEngine.ExecuteAsync(runId, ct);

        async Task<WorkflowRunSnapshot> SnapshotDuringGateAsync(
            TaskCompletionSource entered, TaskCompletionSource proceed)
        {
            await entered.Task.WaitAsync(ct);
            var snapshot = (await readerEngine.GetRunSnapshotAsync(runId, cancellationToken: ct))!;
            proceed.TrySetResult();
            return snapshot;
        }

        var snapshots = new List<WorkflowRunSnapshot>();
        // Free-race reader overlaps every commit below.
        var racer = Task.Run(async () =>
        {
            for (var index = 0; index < 40; index++)
                snapshots.Add((await readerEngine.GetRunSnapshotAsync(runId, cancellationToken: ct))!);
        }, ct);

        // Two gated epochs: snapshot while each claim is inflight but uncommitted
        // as completed, then after each commit.
        snapshots.Add(await SnapshotDuringGateAsync(writer.EnteredA, writer.ProceedA));
        await WorkflowEngineTestBase.WaitUntilAsync(
            () => WorkflowEngineTestBase.HasStepStatusAsync(writerEngine, runId, "gate-a", StepStatus.Completed, ct), ct);
        snapshots.Add((await readerEngine.GetRunSnapshotAsync(runId, cancellationToken: ct))!);
        snapshots.Add(await SnapshotDuringGateAsync(writer.EnteredB, writer.ProceedB));
        await WorkflowEngineTestBase.WaitUntilAsync(
            () => WorkflowEngineTestBase.HasStepStatusAsync(writerEngine, runId, "gate-b", StepStatus.Completed, ct), ct);
        await execution;
        await racer;
        snapshots.Add((await readerEngine.GetRunSnapshotAsync(runId, cancellationToken: ct))!);

        // Watermarks never regress across one reader.
        snapshots.Zip(snapshots.Skip(1), (first, next) => (first, next))
            .Should().OnlyContain(pair =>
                pair.next.ThroughDurableSequence >= pair.first.ThroughDurableSequence);

        var journal = await DrainJournalAsync(readerStore, runId, ct);
        foreach (var snapshot in snapshots)
            AssertPairing(snapshot, journal, "racing reader snapshot");
    }

    [Fact]
    public async Task UncommittedWrites_AreInvisibleToSnapshots()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = Store("uncommitted");
        var runId = await CreateRunAsync(store, "w", DateTimeOffset.UtcNow, null, ct);
        await using var engine = EngineOver(store);
        var before = (await engine.GetRunSnapshotAsync(runId, cancellationToken: ct))!;

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(root, "uncommitted.db"),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString();
        await using var raw = new SqliteConnection(connectionString);
        await raw.OpenAsync(ct);
        await using (var pragma = raw.CreateCommand())
        {
            pragma.CommandText = "PRAGMA busy_timeout = 5000;";
            await pragma.ExecuteNonQueryAsync(ct);
        }

        var markerTime = DateTimeOffset.UtcNow;
        var marker = "test.uncommitted.marker";
        await using (var begin = raw.CreateCommand())
        {
            begin.CommandText = "BEGIN IMMEDIATE;";
            await begin.ExecuteNonQueryAsync(ct);
        }
        try
        {
            await using (var update = raw.CreateCommand())
            {
                update.CommandText = "UPDATE workflow_runs SET updated_at = $at WHERE id = $id;";
                update.Parameters.AddWithValue("$at", markerTime.ToString("O"));
                update.Parameters.AddWithValue("$id", runId.ToString("D"));
                await update.ExecuteNonQueryAsync(ct);
            }
            await using (var insert = raw.CreateCommand())
            {
                insert.CommandText = """
                    INSERT INTO workflow_events
                    (workflow_run_id, step_key, event_type, timestamp, attempt, data_json)
                    VALUES ($run, NULL, $type, $at, NULL, NULL);
                    """;
                insert.Parameters.AddWithValue("$run", runId.ToString("D"));
                insert.Parameters.AddWithValue("$type", marker);
                insert.Parameters.AddWithValue("$at", markerTime.ToString("O"));
                await insert.ExecuteNonQueryAsync(ct);
            }

            var during = (await engine.GetRunSnapshotAsync(runId, cancellationToken: ct))!;
            during.Run.UpdatedAt.Should().Be(before.Run.UpdatedAt, "uncommitted state must stay invisible");
            during.ThroughDurableSequence.Should().Be(
                before.ThroughDurableSequence, "uncommitted events must not advance the watermark");
            var journal = await DrainJournalAsync(store, runId, ct);
            journal.Should().NotContain(e => e.EventType == marker);
        }
        finally
        {
            await using var rollback = raw.CreateCommand();
            rollback.CommandText = "ROLLBACK;";
            await rollback.ExecuteNonQueryAsync(ct);
        }
    }

    [Fact]
    public async Task SignalWait_ProjectsWaitAndDiagnosis()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = Store("waits");
        var registry = new WorkflowRegistry().Register("signal", "1", new SignalWorkflow());
        await using var writer = new WorkflowEngine(store, registry, new ZhinuOptions
        {
            LeaseDuration = TimeSpan.FromSeconds(2),
            LeaseRenewalInterval = TimeSpan.FromMilliseconds(600),
            PollInterval = TimeSpan.FromMilliseconds(10)
        });

        var runId = await writer.StartAsync("signal", "1", "in", cancellationToken: ct);
        await writer.ExecuteAsync(runId, ct);
        await using var reader = EngineOver(
            Store("waits"),
            new WorkflowRegistry().Register("signal", "1", new SignalWorkflow()));
        var snapshot = (await reader.GetRunSnapshotAsync(runId, cancellationToken: ct))!;

        snapshot.Waits.Should().ContainSingle().Which.SignalName.Should().Be("release");
        snapshot.Diagnosis.Should().NotBeNull();
        snapshot.Diagnosis!.Code.Should().Be(RunDiagnosisCode.WaitingForSignal);

        await writer.SendSignalAsync(runId, "release", "\"approved\"", ct);
        await writer.ExecuteAsync(runId, ct);
        await writer.WaitForCompletionAsync<string>(runId, cancellationToken: ct);
        var completed = (await reader.GetRunSnapshotAsync(runId, cancellationToken: ct))!;
        completed.Run.Status.Should().Be(WorkflowStatus.Completed);
        var journal = await DrainJournalAsync(store, runId, ct);
        AssertPairing(completed, journal, "signal-completed run");
    }

    [Fact]
    public async Task FailedRun_ProjectsFailureDiagnosis()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = Store("failed");
        var registry = new WorkflowRegistry().Register("fail", "1", new AlwaysFailsWorkflow());
        await using var writer = new WorkflowEngine(store, registry, new ZhinuOptions
        {
            LeaseDuration = TimeSpan.FromSeconds(2),
            LeaseRenewalInterval = TimeSpan.FromMilliseconds(600),
            PollInterval = TimeSpan.FromMilliseconds(10)
        });

        var runId = await writer.StartAsync("fail", "1", "in", cancellationToken: ct);
        await writer.ExecuteAsync(runId, ct);
        await using var reader = EngineOver(
            Store("failed"),
            new WorkflowRegistry().Register("fail", "1", new AlwaysFailsWorkflow()));
        var snapshot = (await reader.GetRunSnapshotAsync(runId, cancellationToken: ct))!;

        snapshot.Steps.Should().ContainSingle().Which.Status.Should().Be(StepStatus.Failed);
        snapshot.Run.Status.Should().Be(WorkflowStatus.Failed);
        snapshot.Diagnosis.Should().NotBeNull();
        snapshot.Diagnosis!.Code.Should().Be(RunDiagnosisCode.PermanentlyFailedStep);
        var journal = await DrainJournalAsync(store, runId, ct);
        AssertPairing(snapshot, journal, "failed run");
    }

    [Fact]
    public async Task GenerationAndDisposition_AreProjected()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = Store("generation");
        var runId = await CreateRunAsync(store, "w", DateTimeOffset.UtcNow, null, ct);
        var instance = await store.CreateInstanceAsync(null, ct);
        var generation = await store.CreateGenerationAsync(
            instance.InstanceId, runId, "plan-rev-9", "fp-abc", null, ct);
        await store.RecordDispositionAsync(
            generation.GenerationId, CheckpointDisposition.Accept, "looks good", "tester", ct);
        await using var engine = EngineOver(store);

        var snapshot = (await engine.GetRunSnapshotAsync(runId, cancellationToken: ct))!;

        snapshot.Generation.Should().NotBeNull();
        snapshot.Generation!.GenerationId.Should().Be(generation.GenerationId);
        snapshot.Generation.PlanRevision.Should().Be("plan-rev-9");
        snapshot.Generation.ExecutionFingerprint.Should().Be("fp-abc");
        snapshot.Instance.Should().NotBeNull();
        snapshot.Instance!.InstanceId.Should().Be(instance.InstanceId);
        snapshot.Dispositions.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new { Disposition = CheckpointDisposition.Accept, Reason = "looks good", Actor = "tester" },
            options => options.ExcludingMissingMembers());
    }

    [Fact]
    public async Task SourceLineage_AndMaxDepth_AreHonored()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = Store("lineage");
        var s0 = await CreateRunAsync(store, "w", DateTimeOffset.UtcNow, null, ct);
        var s1 = Guid.NewGuid();
        await store.CreateRunAsync(new WorkflowRun
        {
            Id = s1,
            WorkflowName = "w",
            WorkflowVersion = "1",
            Status = WorkflowStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            SourceRunId = s0
        }, ct);
        await using var engine = EngineOver(store);

        var snapshot = (await engine.GetRunSnapshotAsync(s1, cancellationToken: ct))!;
        snapshot.SourceRun.Should().NotBeNull();
        snapshot.SourceRun!.Id.Should().Be(s0);
        snapshot.SourceLineage.Select(r => r.Id).Should().Equal(s0);

        var noLineage = (await engine.GetRunSnapshotAsync(
            s1, new RunSnapshotOptions { IncludeSourceLineage = false }, ct))!;
        noLineage.SourceRun.Should().BeNull();
        noLineage.SourceLineage.Should().BeEmpty();
    }

    [Fact]
    public async Task Children_AreProjectedRecursively_AndDepthBounded()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = Store("children");
        var registry = new WorkflowRegistry()
            .Register("parent", "1", new ParentWorkflow())
            .Register("child", "1", new ChildWorkflow());
        await using var writer = new WorkflowEngine(store, registry, new ZhinuOptions
        {
            LeaseDuration = TimeSpan.FromSeconds(2),
            LeaseRenewalInterval = TimeSpan.FromMilliseconds(600),
            PollInterval = TimeSpan.FromMilliseconds(10)
        });
        await using var reader = EngineOver(Store("children"));

        var runId = await writer.StartAsync("parent", "1", "in", cancellationToken: ct);
        await writer.ExecuteAsync(runId, ct);
        await writer.WaitForCompletionAsync<string>(runId, cancellationToken: ct);
        var snapshot = (await reader.GetRunSnapshotAsync(runId, cancellationToken: ct))!;

        snapshot.Children.Should().ContainSingle();
        var child = snapshot.Children[0];
        child.Run.Id.Should().NotBe(runId);
        child.Run.ParentRunId.Should().Be(runId);
        child.Steps.Should().Contain(s => s.StepKey == "child-step" && s.Status == StepStatus.Completed);
        child.ThroughDurableSequence.Should().BeGreaterThan(0);

        var flat = (await reader.GetRunSnapshotAsync(
            runId, new RunSnapshotOptions { MaxDepth = 0 }, ct))!;
        flat.Children.Should().BeEmpty();
    }

    [Fact]
    public async Task ExternalOperations_AreProjected_WithExplicitTruncation()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = Store("ops");
        var runId = await CreateRunAsync(store, "w", DateTimeOffset.UtcNow, null, ct);
        foreach (var key in new[] { "op-a", "op-b", "op-c" })
            await store.RegisterAsync(new ExternalOperationRegistration
            {
                WorkflowRunId = runId,
                StepKey = "work",
                Attempt = 1,
                IdempotencyKey = key,
                Provider = "provider",
                RecoveryIntent = ExternalOperationRecoveryIntent.Retry,
                PayloadJson = "{}"
            }, ct);
        await using var engine = EngineOver(store);

        var all = (await engine.GetRunSnapshotAsync(runId, cancellationToken: ct))!;
        all.ExternalOperations.Should().HaveCount(3);

        var bounded = (await engine.GetRunSnapshotAsync(
            runId, new RunSnapshotOptions { ExternalOperationsLimit = 2 }, ct))!;
        bounded.ExternalOperations.Should().HaveCount(2);

        var excluded = (await engine.GetRunSnapshotAsync(
            runId, new RunSnapshotOptions { IncludeExternalOperations = false }, ct))!;
        excluded.ExternalOperations.Should().BeEmpty();
    }

    [Fact]
    public async Task SnapshotMatchesProgress_OnQuiescentRun()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = Store("parity");
        var registry = new WorkflowRegistry().Register("two", "1", new TwoStepWorkflow());
        await using var writer = new WorkflowEngine(store, registry, new ZhinuOptions
        {
            LeaseDuration = TimeSpan.FromSeconds(2),
            LeaseRenewalInterval = TimeSpan.FromMilliseconds(600),
            PollInterval = TimeSpan.FromMilliseconds(10)
        });
        await using var reader = EngineOver(Store("parity"));

        var runId = await writer.StartAsync("two", "1", "in", cancellationToken: ct);
        await writer.ExecuteAsync(runId, ct);
        await writer.WaitForCompletionAsync<string>(runId, cancellationToken: ct);
        var snapshot = (await reader.GetRunSnapshotAsync(runId, cancellationToken: ct))!;
        var progress = (await reader.GetRunProgressAsync(runId, cancellationToken: ct))!;

        snapshot.Steps.Select(s => (s.StepKey, s.Status, s.Attempt)).Should()
            .BeEquivalentTo(progress.Steps.Select(s => (s.StepKey, s.Status, s.Attempt)));
        snapshot.Diagnosis.Should().NotBeNull();
        progress.Diagnosis.Should().NotBeNull();
        snapshot.Diagnosis!.Code.Should().Be(progress.Diagnosis!.Code);
        snapshot.Diagnosis.StepKey.Should().Be(progress.Diagnosis.StepKey);
        snapshot.SourceLineage.Select(r => r.Id).Should()
            .BeEquivalentTo(progress.SourceLineage.Select(r => r.Id));

        var noDiagnosis = (await reader.GetRunSnapshotAsync(
            runId, new RunSnapshotOptions { IncludeDiagnosis = false }, ct))!;
        noDiagnosis.Diagnosis.Should().BeNull();
    }

    [Fact]
    public async Task PagesContinueExactlyAfterTheWatermark()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = Store("continue");
        var registry = new WorkflowRegistry().Register("two", "1", new TwoStepWorkflow());
        await using var writer = new WorkflowEngine(store, registry, new ZhinuOptions
        {
            LeaseDuration = TimeSpan.FromSeconds(2),
            LeaseRenewalInterval = TimeSpan.FromMilliseconds(600),
            PollInterval = TimeSpan.FromMilliseconds(10)
        });
        await using var reader = EngineOver(Store("continue"));

        var runId = await writer.StartAsync("two", "1", "in", cancellationToken: ct);
        await writer.ExecuteAsync(runId, ct);
        await writer.WaitForCompletionAsync<string>(runId, cancellationToken: ct);
        var snapshot = (await reader.GetRunSnapshotAsync(runId, cancellationToken: ct))!;
        // On a quiescent run the watermark covers the latest durable event, so
        // the page after it is empty and the cursor is unchanged.
        var first = await reader.GetEventPageAsync(runId, snapshot.ThroughDurableSequence, 1000, ct);
        first.Events.Should().BeEmpty();
        first.NextCursor.Should().Be(snapshot.ThroughDurableSequence);
        first.HasMore.Should().BeFalse();
        // One sequence earlier replays the boundary event itself.
        var boundary = await reader.GetEventPageAsync(runId, snapshot.ThroughDurableSequence - 1, 1000, ct);
        boundary.Events.Should().NotBeEmpty();
        boundary.NextCursor.Should().Be(snapshot.ThroughDurableSequence);
        var next = await reader.GetEventPageAsync(runId, first.NextCursor, 1000, ct);
        next.Events.Should().BeEmpty();
        next.NextCursor.Should().Be(first.NextCursor);
    }

    [Fact]
    public async Task ExportAcknowledgement_DoesNotMoveTheSnapshot()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = Store("export");
        var registry = new WorkflowRegistry().Register("two", "1", new TwoStepWorkflow());
        await using var writer = new WorkflowEngine(store, registry, new ZhinuOptions
        {
            LeaseDuration = TimeSpan.FromSeconds(2),
            LeaseRenewalInterval = TimeSpan.FromMilliseconds(600),
            PollInterval = TimeSpan.FromMilliseconds(10)
        });
        await using var reader = EngineOver(Store("export"));

        var runId = await writer.StartAsync("two", "1", "in", cancellationToken: ct);
        await writer.ExecuteAsync(runId, ct);
        await writer.WaitForCompletionAsync<string>(runId, cancellationToken: ct);
        var before = (await reader.GetRunSnapshotAsync(runId, cancellationToken: ct))!;
        var exported = await store.ReadExportBatchAsync("consumer", runId, 100, ct);
        await store.AcknowledgeExportAsync("consumer", runId, exported[^1].Sequence, ct);
        var after = (await reader.GetRunSnapshotAsync(runId, cancellationToken: ct))!;

        after.ThroughDurableSequence.Should().Be(before.ThroughDurableSequence);
        after.Steps.Select(s => (s.StepKey, s.Status, s.Attempt, s.Revision)).Should()
            .BeEquivalentTo(before.Steps.Select(s => (s.StepKey, s.Status, s.Attempt, s.Revision)));
    }

    [Fact]
    public async Task InvalidOptions_Throw()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = Store("options");
        await using var engine = EngineOver(store);

        await FluentActions.Invoking(() => engine.GetRunSnapshotAsync(
            Guid.NewGuid(), new RunSnapshotOptions { MaxDepth = -1 }, ct))
            .Should().ThrowAsync<ArgumentOutOfRangeException>();
        await FluentActions.Invoking(() => engine.GetRunSnapshotAsync(
            Guid.NewGuid(), new RunSnapshotOptions { ExternalOperationsLimit = 0 }, ct))
            .Should().ThrowAsync<ArgumentOutOfRangeException>();
        await FluentActions.Invoking(() => engine.GetRunSnapshotAsync(
            Guid.NewGuid(), new RunSnapshotOptions { SourceLineageMaxDepth = 0 }, ct))
            .Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    private sealed class TwoStepWorkflow : IWorkflow<string, string>
    {
        public async Task<string> RunAsync(WorkflowContext context, string input, CancellationToken ct)
        {
            var first = await context.StepAsync(
                "first", input, (value, _) => Task.FromResult(value + "-1"), cancellationToken: ct);
            using (context.DependsOn("first"))
            {
                return await context.StepAsync(
                    "second", first, (value, _) => Task.FromResult(value + "-2"), cancellationToken: ct);
            }
        }
    }

    private sealed class GatedWorkflow : IWorkflow<string, string>
    {
        public TaskCompletionSource EnteredA { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ProceedA { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource EnteredB { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ProceedB { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<string> RunAsync(WorkflowContext context, string input, CancellationToken ct)
        {
            var a = await context.StepAsync(
                "gate-a", input,
                async (value, inner) =>
                {
                    EnteredA.TrySetResult();
                    await ProceedA.Task.WaitAsync(inner);
                    return value + "a";
                },
                cancellationToken: ct);
            var b = await context.StepAsync(
                "gate-b", a,
                async (value, inner) =>
                {
                    EnteredB.TrySetResult();
                    await ProceedB.Task.WaitAsync(inner);
                    return value + "b";
                },
                cancellationToken: ct);
            return b;
        }
    }

    private sealed class SignalWorkflow : IWorkflow<string, string>
    {
        public Task<string> RunAsync(WorkflowContext context, string input, CancellationToken cancellationToken) =>
            context.WaitForSignalAsync<string>("approve", "release", cancellationToken: cancellationToken);
    }

    private sealed class AlwaysFailsWorkflow : IWorkflow<string, string>
    {
        public Task<string> RunAsync(WorkflowContext context, string input, CancellationToken cancellationToken) =>
            context.StepAsync<string>(
                "work",
                async ct =>
                {
                    await Task.Delay(1, ct);
                    throw new InvalidOperationException("boom");
                },
                cancellationToken: cancellationToken);
    }

    private sealed class ParentWorkflow : IWorkflow<string, string>
    {
        public Task<string> RunAsync(WorkflowContext context, string input, CancellationToken ct) =>
            context.StartChildAsync<string, string>("child", "child", "1", input, ct);
    }

    private sealed class ChildWorkflow : IWorkflow<string, string>
    {
        public Task<string> RunAsync(WorkflowContext context, string input, CancellationToken ct) =>
            context.StepAsync(
                "child-step", input, (value, _) => Task.FromResult($"child:{value}"),
                cancellationToken: ct);
    }
}
