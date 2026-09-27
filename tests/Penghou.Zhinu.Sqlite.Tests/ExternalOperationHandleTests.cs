using FluentAssertions;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;

namespace Penghou.Zhinu.Sqlite.Tests;

/// <summary>
/// Crash-safe external-operation handles: registration idempotency, fenced
/// acquisition/completion, recovery across repository instances sharing one
/// database file, and generation fencing after restart.
/// </summary>
public sealed class ExternalOperationHandleTests : WorkflowEngineTestBase
{
    [Fact]
    public async Task Register_Get_List_RoundTrip()
    {
        var ct = TestContext.Current.CancellationToken;
        var repository = Repository();
        var runId = await StartRunAsync(ct);
        var handle = await repository.RegisterAsync(new ExternalOperationRegistration
        {
            WorkflowRunId = runId,
            StepKey = "approval",
            Attempt = 1,
            IdempotencyKey = "op:1",
            Provider = "codex",
            ExternalId = "thread-1",
            RecoveryIntent = ExternalOperationRecoveryIntent.Resume,
            PayloadJson = "{\"a\":1}"
        }, ct);

        handle.Status.Should().Be(ExternalOperationStatus.Requested);
        handle.OwnerId.Should().BeNull();
        handle.LeaseGeneration.Should().Be(1);
        (await repository.GetAsync(handle.OperationId, ct)).Should().BeEquivalentTo(handle);
        (await repository.ListAsync(runId, cancellationToken: ct)).Should().ContainSingle()
            .Which.OperationId.Should().Be(handle.OperationId);
    }

    [Fact]
    public async Task Register_SameKey_ReturnsOriginal()
    {
        var ct = TestContext.Current.CancellationToken;
        var repository = Repository();
        var runId = await StartRunAsync(ct);
        var request = new ExternalOperationRegistration
        {
            WorkflowRunId = runId,
            IdempotencyKey = "op:dup",
            Provider = "codex",
            RecoveryIntent = ExternalOperationRecoveryIntent.Resume
        };

        var first = await repository.RegisterAsync(request, ct);
        var second = await repository.RegisterAsync(request, ct);

        second.OperationId.Should().Be(first.OperationId);
        (await repository.ListAsync(runId, cancellationToken: ct)).Should().ContainSingle();
    }

    [Fact]
    public async Task Register_SameKeyDifferentIntent_Conflicts()
    {
        var ct = TestContext.Current.CancellationToken;
        var repository = Repository();
        var runId = await StartRunAsync(ct);
        var request = new ExternalOperationRegistration
        {
            WorkflowRunId = runId,
            IdempotencyKey = "op:ambiguous",
            Provider = "codex",
            RecoveryIntent = ExternalOperationRecoveryIntent.Resume
        };
        var registered = await repository.RegisterAsync(request, ct);

        var act = () => repository.RegisterAsync(
            request with { Provider = "other" }, ct).AsTask();

        (await act.Should().ThrowAsync<WorkflowOperationConflictException>())
            .Which.OperationId.Should().Be(registered.OperationId);
    }

    [Fact]
    public async Task Acquire_Complete_HappyPath()
    {
        var ct = TestContext.Current.CancellationToken;
        var repository = Repository();
        var runId = await StartRunAsync(ct);
        var registered = await repository.RegisterAsync(new ExternalOperationRegistration
        {
            WorkflowRunId = runId,
            IdempotencyKey = "op:ok",
            Provider = "codex",
            RecoveryIntent = ExternalOperationRecoveryIntent.Resume
        }, ct);

        var acquired = await repository.AcquireAsync(
            registered.OperationId, "worker-1", registered.LeaseGeneration, ct);
        acquired.Status.Should().Be(ExternalOperationStatus.Running);
        acquired.OwnerId.Should().Be("worker-1");

        var completed = await repository.CompleteAsync(
            registered.OperationId, "worker-1", "{\"done\":true}", ct);
        completed.Status.Should().Be(ExternalOperationStatus.Completed);
        completed.PayloadJson.Should().Be("{\"done\":true}");
        completed.CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Acquire_Twice_SecondFails()
    {
        var ct = TestContext.Current.CancellationToken;
        var repository = Repository();
        var runId = await StartRunAsync(ct);
        var registered = await repository.RegisterAsync(new ExternalOperationRegistration
        {
            WorkflowRunId = runId,
            Provider = "codex",
            RecoveryIntent = ExternalOperationRecoveryIntent.Retry
        }, ct);
        await repository.AcquireAsync(
            registered.OperationId, "worker-1", registered.LeaseGeneration, ct);

        var act = () => repository.AcquireAsync(
            registered.OperationId, "worker-2", registered.LeaseGeneration, ct).AsTask();

        await act.Should().ThrowAsync<LeaseLostException>();
    }

    [Fact]
    public async Task Complete_WrongOwner_Fails()
    {
        var ct = TestContext.Current.CancellationToken;
        var repository = Repository();
        var runId = await StartRunAsync(ct);
        var registered = await repository.RegisterAsync(new ExternalOperationRegistration
        {
            WorkflowRunId = runId,
            Provider = "codex",
            RecoveryIntent = ExternalOperationRecoveryIntent.Resume
        }, ct);
        await repository.AcquireAsync(
            registered.OperationId, "worker-1", registered.LeaseGeneration, ct);

        var act = () => repository.CompleteAsync(
            registered.OperationId, "worker-2", null, ct).AsTask();

        await act.Should().ThrowAsync<LeaseLostException>();
        (await repository.GetAsync(registered.OperationId, ct))!
            .Status.Should().Be(ExternalOperationStatus.Running);
    }

    [Fact]
    public async Task Complete_Twice_SecondFails()
    {
        var ct = TestContext.Current.CancellationToken;
        var repository = Repository();
        var runId = await StartRunAsync(ct);
        var registered = await repository.RegisterAsync(new ExternalOperationRegistration
        {
            WorkflowRunId = runId,
            Provider = "codex",
            RecoveryIntent = ExternalOperationRecoveryIntent.Resume
        }, ct);
        await repository.AcquireAsync(
            registered.OperationId, "worker-1", registered.LeaseGeneration, ct);
        await repository.CompleteAsync(registered.OperationId, "worker-1", null, ct);

        var act = () => repository.CompleteAsync(
            registered.OperationId, "worker-1", null, ct).AsTask();

        await act.Should().ThrowAsync<LeaseLostException>();
    }

    [Fact]
    public async Task Fail_RecordsError()
    {
        var ct = TestContext.Current.CancellationToken;
        var repository = Repository();
        var runId = await StartRunAsync(ct);
        var registered = await repository.RegisterAsync(new ExternalOperationRegistration
        {
            WorkflowRunId = runId,
            Provider = "codex",
            RecoveryIntent = ExternalOperationRecoveryIntent.Abandon
        }, ct);
        await repository.AcquireAsync(
            registered.OperationId, "worker-1", registered.LeaseGeneration, ct);

        var failed = await repository.FailAsync(
            registered.OperationId, "worker-1", "boom", ct);

        failed.Status.Should().Be(ExternalOperationStatus.Failed);
        failed.Error.Should().Be("boom");
        failed.CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task CrashBeforeAcquire_ResumeAcquireComplete()
    {
        var ct = TestContext.Current.CancellationToken;
        var runId = await StartRunAsync(ct);
        var crashed = Repository();
        var registered = await crashed.RegisterAsync(new ExternalOperationRegistration
        {
            WorkflowRunId = runId,
            StepKey = "approval",
            Attempt = 1,
            IdempotencyKey = "op:crash-before",
            Provider = "codex",
            ExternalId = "thread-9",
            RecoveryIntent = ExternalOperationRecoveryIntent.Resume
        }, ct);

        // Simulate a process kill: abandon the repository and reopen.
        var recovered = Repository();
        var found = await recovered.GetAsync(registered.OperationId, ct);

        found.Should().NotBeNull();
        found!.Status.Should().Be(ExternalOperationStatus.Requested);
        found.ExternalId.Should().Be("thread-9");
        var completed = await recovered.CompleteAsync(
            (await recovered.AcquireAsync(
                registered.OperationId, "worker-2", found.LeaseGeneration, ct)).OperationId,
            "worker-2", null, ct);
        completed.Status.Should().Be(ExternalOperationStatus.Completed);
    }

    [Fact]
    public async Task CrashAfterAcquire_ResumeCompletes()
    {
        var ct = TestContext.Current.CancellationToken;
        var runId = await StartRunAsync(ct);
        var crashed = Repository();
        var registered = await crashed.RegisterAsync(new ExternalOperationRegistration
        {
            WorkflowRunId = runId,
            IdempotencyKey = "op:crash-after",
            Provider = "codex",
            RecoveryIntent = ExternalOperationRecoveryIntent.Resume
        }, ct);
        await crashed.AcquireAsync(
            registered.OperationId, "worker-1", registered.LeaseGeneration, ct);

        var recovered = Repository();
        var found = await recovered.GetAsync(registered.OperationId, ct);

        found.Should().NotBeNull();
        found!.Status.Should().Be(ExternalOperationStatus.Running);
        var completed = await recovered.CompleteAsync(
            registered.OperationId, "worker-1", "{\"done\":true}", ct);
        completed.Status.Should().Be(ExternalOperationStatus.Completed);

        var hijack = () => recovered.CompleteAsync(
            registered.OperationId, "worker-2", null, ct).AsTask();
        await hijack.Should().ThrowAsync<LeaseLostException>();
    }

    [Fact]
    public async Task Restart_AbandonsPendingAcquisition()
    {
        var ct = TestContext.Current.CancellationToken;
        var engine = CreateEngine(new SignalWorkflow(), "fence-op-restart");
        var runId = await engine.StartAsync(
            "fence-op-restart", "1", "x", cancellationToken: ct);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var execution = engine.ExecuteAsync(runId, cts.Token);
        await WaitUntilAsync(
            () => HasStepStatusAsync(engine, runId, "approval", StepStatus.Waiting, cts.Token),
            cts.Token);

        var repository = Repository();
        var registered = await repository.RegisterAsync(new ExternalOperationRegistration
        {
            WorkflowRunId = runId,
            Provider = "codex",
            RecoveryIntent = ExternalOperationRecoveryIntent.Resume
        }, ct);

        await engine.RestartStepAsync(runId, "approval", cts.Token);
        await cts.CancelAsync();
        try { await execution; } catch { }

        var act = () => repository.AcquireAsync(
            registered.OperationId, "worker-1", registered.LeaseGeneration, ct).AsTask();

        // The registration captured the pre-restart generation; acquisition
        // after the restart fails closed.
        await act.Should().ThrowAsync<LeaseLostException>();
    }

    [Fact]
    public async Task MissingRun_Register_Fails()
    {
        var ct = TestContext.Current.CancellationToken;
        var repository = Repository();

        var act = () => repository.RegisterAsync(new ExternalOperationRegistration
        {
            WorkflowRunId = Guid.NewGuid(),
            Provider = "codex",
            RecoveryIntent = ExternalOperationRecoveryIntent.Resume
        }, ct).AsTask();

        await act.Should().ThrowAsync<WorkflowNotFoundException>();
    }

    [Fact]
    public async Task MissingOperation_LookupAndTransitions_Fail()
    {
        var ct = TestContext.Current.CancellationToken;
        var repository = Repository();
        var missing = Guid.NewGuid();

        (await repository.GetAsync(missing, ct)).Should().BeNull();
        await repository.Invoking(value => value.AcquireAsync(
                missing, "worker-1", 1, ct).AsTask())
            .Should().ThrowAsync<WorkflowNotFoundException>();
        await repository.Invoking(value => value.CompleteAsync(
                missing, "worker-1", null, ct).AsTask())
            .Should().ThrowAsync<WorkflowNotFoundException>();
        await repository.Invoking(value => value.FailAsync(
                missing, "worker-1", "boom", ct).AsTask())
            .Should().ThrowAsync<WorkflowNotFoundException>();
    }

    private SqliteExternalOperationRepository Repository() =>
        new(new ZhinuSqliteOptions
        {
            DatabasePath = Path.Combine(root, "zhinu.db"),
            BusyTimeout = TimeSpan.FromSeconds(2),
            Pooling = false
        });

    private async Task<Guid> StartRunAsync(CancellationToken ct)
    {
        var name = $"extop-{Guid.NewGuid():N}";
        var engine = CreateEngine(new SignalWorkflow(), name);
        return await engine.StartAsync(name, "1", "x", cancellationToken: ct);
    }
}


