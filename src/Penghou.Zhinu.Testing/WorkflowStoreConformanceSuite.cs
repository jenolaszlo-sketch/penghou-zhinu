namespace Penghou.Zhinu.Testing;

/// <summary>
/// A reusable, store-agnostic certification suite for <see cref="IWorkflowStore"/>
/// implementations. Each capability group is checked against the fixture's
/// primary store and, where it matters, a second independent instance over the
/// same backing store, approximating separate hosts. A future PostgreSQL- or
/// Redis-backed store can run the same suite without modification.
/// </summary>
public static class WorkflowStoreConformanceSuite
{
    /// <summary>Runs every capability group and returns a per-group report.</summary>
    public static async Task<WorkflowStoreConformanceReport> VerifyAsync(
        IWorkflowStoreFixture fixture,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        var groups = new List<WorkflowConformanceGroupResult>();
        var checks = new (WorkflowConformanceCapability Capability, Func<CancellationToken, Task> Check)[]
        {
            (WorkflowConformanceCapability.Core, ct => VerifyCoreAsync(fixture, ct)),
            (WorkflowConformanceCapability.Concurrency, ct => VerifyConcurrencyAsync(fixture, ct)),
            (WorkflowConformanceCapability.Fencing, ct => VerifyFencingAsync(fixture, ct)),
            (WorkflowConformanceCapability.Recovery, ct => VerifyRecoveryAsync(fixture, ct)),
            (WorkflowConformanceCapability.Signals, ct => VerifySignalsAsync(fixture, ct)),
            (WorkflowConformanceCapability.Artifacts, ct => VerifyArtifactsAsync(fixture, ct)),
            (WorkflowConformanceCapability.Children, ct => VerifyChildrenAsync(fixture, ct)),
            (WorkflowConformanceCapability.Transactions, ct => VerifyTransactionsAsync(fixture, ct))
        };
        foreach (var (capability, check) in checks)
        {
            try
            {
                await check(cancellationToken).ConfigureAwait(false);
                groups.Add(new WorkflowConformanceGroupResult(capability, true, null));
            }
            catch (Exception exception)
            {
                groups.Add(new WorkflowConformanceGroupResult(capability, false, exception));
            }
        }
        return new WorkflowStoreConformanceReport { Groups = groups };
    }

    private static WorkflowEngine CreateEngine(
        IWorkflowStore store,
        WorkflowRegistry registry,
        TimeProvider timeProvider,
        ZhinuOptions options)
    {
        options.Validate();
        return new WorkflowEngine(
            store,
            registry,
            options,
            serializerOptions: null,
            timeProvider: timeProvider);
    }

    private static WorkflowRegistry Registry(params (string Name, string Version, IWorkflow<string, string> Workflow)[] registrations)
    {
        var registry = new WorkflowRegistry();
        foreach (var (name, version, workflow) in registrations)
            registry.Register(name, version, workflow);
        return registry;
    }

    private static ZhinuOptions FastOptions() => new()
    {
        PollInterval = TimeSpan.FromMilliseconds(10),
        LeaseDuration = TimeSpan.FromSeconds(2),
        LeaseRenewalInterval = TimeSpan.FromMilliseconds(600)
    };

    private static async Task VerifyCoreAsync(IWorkflowStoreFixture fixture, CancellationToken ct)
    {
        var engine = CreateEngine(fixture.Store, Registry(("core", "1", new TwoStep())), fixture.TimeProvider, FastOptions());
        var runId = await engine.StartAsync("core", "1", "x", cancellationToken: ct);
        await engine.ExecuteAsync(runId, ct);
        var result = await engine.WaitForCompletionAsync<string>(runId, cancellationToken: ct);
        if (result != "Hello, x!")
            throw new InvalidOperationException($"Unexpected core result '{result}'.");
        var run = await engine.GetRunAsync(runId, ct);
        if (run is null || run.Status != WorkflowStatus.Completed)
            throw new InvalidOperationException("Core run did not complete.");
        var steps = await engine.GetStepsAsync(runId, ct);
        if (steps.Count != 2 || steps.All(s => s.Status != StepStatus.Completed))
            throw new InvalidOperationException("Core steps did not all complete.");
    }

    private static async Task VerifyConcurrencyAsync(IWorkflowStoreFixture fixture, CancellationToken ct)
    {
        var engineA = CreateEngine(fixture.Store, Registry(("conc", "1", new TwoStep())), fixture.TimeProvider, FastOptions());
        var engineB = CreateEngine(fixture.CreatePeerStore(), Registry(("conc", "1", new TwoStep())), fixture.TimeProvider, FastOptions());
        var runId = await engineA.StartAsync("conc", "1", "x", cancellationToken: ct);
        await Task.WhenAll(
            engineA.ExecuteAsync(runId, ct),
            engineB.ExecuteAsync(runId, ct));
        var result = await engineA.WaitForCompletionAsync<string>(runId, cancellationToken: ct);
        if (result != "Hello, x!")
            throw new InvalidOperationException($"Concurrent execution produced '{result}'.");
    }

    private static async Task VerifyFencingAsync(IWorkflowStoreFixture fixture, CancellationToken ct)
    {
        var engine = CreateEngine(fixture.Store, Registry(("fence", "1", new TwoStep())), fixture.TimeProvider, FastOptions());
        var runId = await engine.StartAsync("fence", "1", "x", cancellationToken: ct);
        await engine.ExecuteAsync(runId, ct);
        await engine.WaitForCompletionAsync<string>(runId, cancellationToken: ct);
        var staleStep = (await engine.GetStepsAsync(runId, ct))
            .Single(s => s.StepKey == "first" && s.Status == StepStatus.Completed);
        await engine.RestartStepAsync(runId, "first", cancellationToken: ct);
        var after = await engine.GetStepsAsync(runId, ct);
        if (!after.Any(s => s.StepKey == "first" && s.Status == StepStatus.Pending))
            throw new InvalidOperationException("Restart did not reset the target step to a fresh Pending revision.");
        var runAfterRestart = await engine.GetRunAsync(runId, ct);
        if (runAfterRestart is null || runAfterRestart.Status != WorkflowStatus.Pending)
            throw new InvalidOperationException("Restart did not reset the run to Pending.");
        // A stale write to the previous revision must be rejected: the step is no
        // longer Running and the run generation has moved on.
        try
        {
            await fixture.Store.CompleteStepAsync(staleStep.Id, "stale-owner", "x", DateTimeOffset.UtcNow, ct);
            throw new InvalidOperationException("A stale generation completed a fenced-out step.");
        }
        catch (WorkflowStateException)
        {
            // Expected: fencing rejected the stale write.
        }
    }

    private static async Task VerifyRecoveryAsync(IWorkflowStoreFixture fixture, CancellationToken ct)
    {
        var registry = Registry(("recover", "1", new SlowStep()));
        var options = new ZhinuOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(10),
            LeaseDuration = TimeSpan.FromMilliseconds(150),
            LeaseRenewalInterval = TimeSpan.FromMilliseconds(40)
        };
        var engineA = CreateEngine(fixture.Store, registry, fixture.TimeProvider, options);
        var runId = await engineA.StartAsync("recover", "1", "x", cancellationToken: ct);
        using var interruption = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var execution = engineA.ExecuteAsync(runId, interruption.Token);
        await Task.Delay(400, ct);
        // Simulate process death: stop the worker so it stops renewing leases.
        await interruption.CancelAsync();
        try { await execution; } catch { }

        // Let the step lease expire, then a fresh instance recovers and completes.
        await Task.Delay(300, ct);
        var engineB = CreateEngine(fixture.CreatePeerStore(), registry, fixture.TimeProvider, FastOptions());
        await engineB.RunAvailableAsync(ct);
        var result = await engineB.WaitForCompletionAsync<string>(runId, cancellationToken: ct);
        if (result != "recovered")
            throw new InvalidOperationException($"Recovery produced '{result}'.");
    }

    private static async Task VerifySignalsAsync(IWorkflowStoreFixture fixture, CancellationToken ct)
    {
        var engine = CreateEngine(fixture.Store, Registry(("signal", "1", new SignalWait())), fixture.TimeProvider, FastOptions());
        var runId = await engine.StartAsync("signal", "1", "x", cancellationToken: ct);
        // Buffer two signals before the wait exists.
        await engine.SendSignalAsync(runId, "approve", "first", ct);
        await engine.SendSignalAsync(runId, "approve", "second", ct);
        await engine.ExecuteAsync(runId, ct);
        var result = await engine.WaitForCompletionAsync<string>(runId, cancellationToken: ct);
        var events = await engine.GetEventsAsync(runId, cancellationToken: ct);
        var delivered = events.Count(e => e.EventType == WorkflowEventTypes.SignalDelivered);
        if (delivered != 1)
            throw new InvalidOperationException($"Signal delivered {delivered} times (expected exactly once).");
    }

    private static async Task VerifyArtifactsAsync(IWorkflowStoreFixture fixture, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var run = new WorkflowRun
        {
            Id = Guid.NewGuid(),
            WorkflowName = "artifact-conformance",
            WorkflowVersion = "1",
            Status = WorkflowStatus.Pending,
            CreatedAt = now,
            UpdatedAt = now
        };
        await fixture.Store.InitializeAsync(ct);
        await fixture.Store.CreateRunAsync(run, ct);
        var request = new ArtifactPublicationRequest
        {
            WorkflowRunId = run.Id,
            Now = now,
            Artifact = new WorkflowArtifactDescriptor
            {
                Name = "result",
                ArtifactType = "application/octet-stream",
                Location = "conformance://artifact/result",
                ContentHash = "sha256:conformance"
            }
        };
        var first = await fixture.Store.PublishArtifactAsync(request, ct);
        var repeated = await fixture.Store.PublishArtifactAsync(request, ct);
        var latest = await fixture.Store.GetLatestArtifactAsync(run.Id, "result", ct);
        if (!first.Created || repeated.Created || latest?.Id != first.Artifact.Id)
            throw new InvalidOperationException("Artifact publish was not idempotent.");
        var listed = await fixture.Store.GetArtifactsAsync(run.Id, ct);
        if (listed.Count != 1)
            throw new InvalidOperationException("Artifact list did not round-trip.");
    }

    private static async Task VerifyChildrenAsync(IWorkflowStoreFixture fixture, CancellationToken ct)
    {
        var registry = Registry(
            ("parent", "1", new Parent()),
            ("child", "1", new Child()));
        var engine = CreateEngine(fixture.Store, registry, fixture.TimeProvider, FastOptions());
        var runId = await engine.StartAsync("parent", "1", "x", cancellationToken: ct);
        await engine.ExecuteAsync(runId, ct);
        await engine.WaitForCompletionAsync<string>(runId, cancellationToken: ct);
        var subtree = await fixture.Store.GetRunSubtreeAsync(runId, 5, ct);
        var children = subtree.Where(r => r.ParentRunId == runId).ToList();
        if (children.Count != 1)
            throw new InvalidOperationException($"Expected one child, found {children.Count}.");
        var childId = children[0].Id;
        await engine.RestartStepAsync(runId, "child:start", cancellationToken: ct);
        await engine.ExecuteAsync(runId, ct);
        await engine.WaitForCompletionAsync<string>(runId, cancellationToken: ct);
        var subtree2 = await fixture.Store.GetRunSubtreeAsync(runId, 5, ct);
        var children2 = subtree2.Where(r => r.ParentRunId == runId).ToList();
        if (children2.Count != 2 || !children2.Any(r => r.Id == childId))
            throw new InvalidOperationException("Child identity was not deterministic or restart did not fork a new child.");
    }

    private static async Task VerifyTransactionsAsync(IWorkflowStoreFixture fixture, CancellationToken ct)
    {
        var engine = CreateEngine(fixture.Store, Registry(("tx", "1", new RollbackWorkflow())), fixture.TimeProvider, FastOptions());
        var runId = await engine.StartAsync("tx", "1", "x", cancellationToken: ct);
        await engine.ExecuteAsync(runId, ct);
        await engine.WaitForCompletionAsync<string>(runId, cancellationToken: ct);
        var plan1 = await engine.PlanRollbackAsync(runId, cancellationToken: ct);
        var plan2 = await engine.PlanRollbackAsync(runId, cancellationToken: ct);
        if (!plan1.Steps.Select(s => s.StepKey).SequenceEqual(plan2.Steps.Select(s => s.StepKey)))
            throw new InvalidOperationException("Rollback planning was not deterministic.");
        await engine.RollbackAsync(runId, cancellationToken: ct);
        var run = await engine.GetRunAsync(runId, ct);
        if (run is null || run.Status != WorkflowStatus.Compensated)
            throw new InvalidOperationException("Rollback did not reach Compensated.");
    }

    private sealed class TwoStep : IWorkflow<string, string>
    {
        public async Task<string> RunAsync(WorkflowContext context, string input, CancellationToken ct)
        {
            var first = await context.StepAsync("first", input, (v, _) => Task.FromResult($"Hello, {v}"), cancellationToken: ct);
            return await context.StepAsync("second", first, (v, _) => Task.FromResult($"{v}!"), cancellationToken: ct);
        }
    }

    private sealed class SlowStep : IWorkflow<string, string>
    {
        public async Task<string> RunAsync(WorkflowContext context, string input, CancellationToken ct)
        {
            return await context.StepAsync(
                "slow",
                async (_, innerCt) =>
                {
                    await Task.Delay(1500, innerCt);
                    return "recovered";
                },
                cancellationToken: ct);
        }
    }

    private sealed class SignalWait : IWorkflow<string, string>
    {
        public async Task<string> RunAsync(WorkflowContext context, string input, CancellationToken ct) =>
            await context.WaitForSignalAsync<string>("approval", "approve", cancellationToken: ct);
    }

    private sealed class Parent : IWorkflow<string, string>
    {
        public async Task<string> RunAsync(WorkflowContext context, string input, CancellationToken ct) =>
            await context.StartChildAsync<string, string>("child", "child", "1", input, ct);
    }

    private sealed class Child : IWorkflow<string, string>
    {
        public async Task<string> RunAsync(WorkflowContext context, string input, CancellationToken ct) =>
            await context.StepAsync("child-step", input, (v, _) => Task.FromResult($"child:{v}"), cancellationToken: ct);
    }

    private sealed class RollbackWorkflow : IWorkflow<string, string>
    {
        public async Task<string> RunAsync(WorkflowContext context, string input, CancellationToken ct)
        {
            var plan = await context.StepAsync("plan", input, (v, _) => Task.FromResult($"plan:{v}"), cancellationToken: ct);
            return await context.StepAsync(
                "deploy",
                plan,
                (v, _) => Task.FromResult($"deployed:{v}"),
                compensation: (result, step, innerCt) => Task.CompletedTask,
                cancellationToken: ct);
        }
    }
}
