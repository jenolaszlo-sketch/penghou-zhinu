using FluentAssertions;
using Microsoft.Data.Sqlite;
using Penghou.Workflow.Abstractions;

namespace Penghou.Zhinu.Sqlite.Tests;

public sealed class WorkflowAuthorizationRecoveryTests : WorkflowEngineTestBase
{
    [Fact]
    public async Task ApprovalWake_RequiresExactCorrelation_IsIdempotent_AndReauthorizesBeforeDispatch()
    {
        var calls = 0;
        var authorizer = new RecordingAuthorizer((context, provider, attempt) => attempt == 1
            ? Approval(context, provider, "approval-1")
            : Result(ExecutionAuthorizationDecision.Denied, context, provider));
        var (engine, workflow, store, runId) = await StartProtectedAsync(authorizer,
            onCallback: () => calls++);
        await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
        var step = (await engine.GetStepsAsync(runId, TestContext.Current.CancellationToken))
            .Single(item => item.StepKey == "protected");
        var pending = await store.GetPendingAuthorizationAsync(
            runId, step.Id, false, TestContext.Current.CancellationToken);
        pending.Should().NotBeNull();

        var exact = Wake(pending!, approvalId: "approval-1");
        (await store.WakeAuthorizationAsync(exact with { ApprovalRequestId = "wrong" },
            TestContext.Current.CancellationToken)).Should().BeFalse();
        (await store.WakeAuthorizationAsync(exact with { AuthorizationRequestId = "stale-request" },
            TestContext.Current.CancellationToken)).Should().BeFalse();
        (await store.WakeAuthorizationAsync(exact,
            TestContext.Current.CancellationToken)).Should().BeTrue();
        (await store.WakeAuthorizationAsync(exact,
            TestContext.Current.CancellationToken)).Should().BeTrue();

        await engine.DisposeAsync();
        await using var resumedEngine = new WorkflowEngine(store,
            new WorkflowRegistry().Register("authorized", "1", workflow), Options(authorizer));
        await resumedEngine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
        calls.Should().Be(0);
        workflow.Calls.Should().Be(0);
        authorizer.Requests.Should().HaveCount(2);
        authorizer.Requests.Select(item => item.AuthorizationRequestId).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task RestartInvalidatesOldApprovalWake_AndUsesFreshAuthorization()
    {
        var authorizer = new RecordingAuthorizer((context, provider, attempt) => attempt == 1
            ? Approval(context, provider, "approval-old")
            : Result(ExecutionAuthorizationDecision.Allowed, context, provider,
                expiresAt: DateTimeOffset.UtcNow.AddMinutes(1)));
        var (engine, workflow, store, runId) = await StartProtectedAsync(authorizer);
        await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
        var step = (await engine.GetStepsAsync(runId, TestContext.Current.CancellationToken))
            .Single(item => item.StepKey == "protected");
        var pending = await store.GetPendingAuthorizationAsync(
            runId, step.Id, false, TestContext.Current.CancellationToken);
        pending.Should().NotBeNull();

        await engine.RestartStepAsync(runId, "protected", TestContext.Current.CancellationToken);
        (await store.WakeAuthorizationAsync(Wake(pending!, "approval-old"),
            TestContext.Current.CancellationToken)).Should().BeFalse();
        await engine.DisposeAsync();
        await using var resumedEngine = new WorkflowEngine(store,
            new WorkflowRegistry().Register("authorized", "1", workflow), Options(authorizer));
        await resumedEngine.ExecuteAsync(runId, TestContext.Current.CancellationToken);

        workflow.Calls.Should().Be(1);
        authorizer.Requests.Should().HaveCount(2);
        authorizer.Requests[1].AuthorizationRequestId.Should().NotBe(pending!.Context.AuthorizationRequestId);
        authorizer.Requests[1].Identity.ExecutionRevision.Should().NotBe(pending.Context.Identity.ExecutionRevision);
    }

    [Fact]
    public async Task DeclarationDriftWhileParked_FailsClosedAndDoesNotDispatch()
    {
        var workflow = new MutableDeclarationWorkflow();
        var authorizer = new RecordingAuthorizer((context, provider, _) =>
            Approval(context, provider, "approval-drift"));
        var (engine, _, store, runId) = await StartAsync(workflow, authorizer);
        await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
        var originalStep = (await engine.GetStepsAsync(runId, TestContext.Current.CancellationToken))
            .Single(item => item.StepKey == "protected");
        var pending = await store.GetPendingAuthorizationAsync(
            runId, originalStep.Id, false, TestContext.Current.CancellationToken);
        pending.Should().NotBeNull();
        (await store.WakeAuthorizationAsync(Wake(pending!, "approval-drift"),
            TestContext.Current.CancellationToken)).Should().BeTrue();
        workflow.Authorization = Declaration("write");
        await engine.DisposeAsync();
        await using var resumedEngine = new WorkflowEngine(
            store,
            new WorkflowRegistry().Register("authorized", "1", workflow),
            Options(authorizer));
        await resumedEngine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
        workflow.Calls.Should().Be(0);
        authorizer.Requests.Should().ContainSingle();
        (await resumedEngine.GetRunAsync(runId, TestContext.Current.CancellationToken))!.Status
            .Should().Be(WorkflowStatus.Failed);
    }

    [Fact]
    public async Task CompletedProtectedHistory_ReusesResultWithoutFreshAuthorization()
    {
        var authorizer = new RecordingAuthorizer((context, provider, _) =>
            Result(ExecutionAuthorizationDecision.Allowed, context, provider,
                expiresAt: DateTimeOffset.UtcNow.AddMinutes(1)));
        var workflow = new ProtectedThenTailWorkflow();
        var (engine, _, _, runId) = await StartAsync(workflow, authorizer);
        await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
        await engine.RestartStepAsync(runId, "tail", TestContext.Current.CancellationToken);
        await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);

        workflow.ProtectedCalls.Should().Be(1);
        workflow.TailCalls.Should().Be(2);
        authorizer.Requests.Count(request => request.Identity.OperationPath == "protected")
            .Should().Be(1);
    }

    [Fact]
    public async Task DecisionExpiringDuringDatabaseWriterWait_BlocksProtectedCallback()
    {
        var store = CreateStore();
        var authorizer = new WriterLockAuthorizer(Path.Combine(root, "zhinu.db"));
        var workflow = new ProtectedWorkflow();
        var registry = new WorkflowRegistry().Register("authorized", "1", workflow);
        var options = Options(authorizer);
        await using var engine = new WorkflowEngine(store, registry, options);
        var runId = await engine.StartAsync("authorized", "1", "input",
            cancellationToken: TestContext.Current.CancellationToken);
        var execute = engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
        await authorizer.LockAcquired.Task.WaitAsync(TestContext.Current.CancellationToken);
        await Task.Delay(400, TestContext.Current.CancellationToken);
        authorizer.ReleaseWriterLock();
        await execute;

        workflow.Calls.Should().Be(0);
    }

    [Fact]
    public async Task ChildStartAndWaitAreAuthorized_ChildActivitySeesParent_AndCanBeDenied()
    {
        var authorizer = new RecordingAuthorizer((context, provider, _) =>
        {
            var capability = context.Requirements.Single().Capability;
            var decision = capability == "child.execute"
                ? ExecutionAuthorizationDecision.Denied
                : ExecutionAuthorizationDecision.Allowed;
            return Result(decision, context, provider,
                expiresAt: decision == ExecutionAuthorizationDecision.Allowed
                    ? DateTimeOffset.UtcNow.AddMinutes(1) : null);
        });
        var store = CreateStore();
        var parent = new ChildParentWorkflow();
        var child = new ProtectedWorkflow(capability: "child.execute");
        var registry = new WorkflowRegistry()
            .Register("parent", "1", parent)
            .Register("child", "1", child);
        await using var engine = new WorkflowEngine(store, registry, Options(authorizer));
        var runId = await engine.StartAsync("parent", "1", "input",
            cancellationToken: TestContext.Current.CancellationToken);
        await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);

        child.Calls.Should().Be(0);
        var parentRequests = authorizer.Requests
            .Where(request => request.Identity.ExecutionId == runId.ToString("N")).ToArray();
        parentRequests.SelectMany(request => request.Requirements)
            .Select(requirement => requirement.Capability).Should().Contain("child.start")
            .And.Contain("child.wait");
        var childRequest = authorizer.Requests.Single(request =>
            request.Requirements.Any(requirement => requirement.Capability == "child.execute"));
        childRequest.Identity.ParentExecutionId.Should().Be(runId.ToString("N"));
    }

    private async Task<(WorkflowEngine Engine, ProtectedWorkflow Workflow, SqliteWorkflowStore Store, Guid RunId)>
        StartProtectedAsync(RecordingAuthorizer authorizer, Action? onCallback = null)
    {
        var workflow = new ProtectedWorkflow(onCallback);
        var (engine, _, store, runId) = await StartAsync(workflow, authorizer);
        return (engine, workflow, store, runId);
    }

    private async Task<(WorkflowEngine Engine, TWorkflow Workflow, SqliteWorkflowStore Store, Guid RunId)>
        StartAsync<TWorkflow>(TWorkflow workflow, RecordingAuthorizer authorizer)
        where TWorkflow : class, IWorkflow<string, string>
    {
        var store = CreateStore();
        var engine = new WorkflowEngine(store,
            new WorkflowRegistry().Register("authorized", "1", workflow),
            Options(authorizer));
        var runId = await engine.StartAsync("authorized", "1", "input",
            cancellationToken: TestContext.Current.CancellationToken);
        return (engine, workflow, store, runId);
    }

    private static ZhinuOptions Options(IExecutionAuthorizer authorizer) => new()
    {
        PollInterval = TimeSpan.FromMilliseconds(10),
        LeaseDuration = TimeSpan.FromSeconds(3),
        LeaseRenewalInterval = TimeSpan.FromMilliseconds(500),
        ExecutionAuthorization = new WorkflowExecutionAuthorizationOptions("policy", "host-v1", authorizer)
    };

    private static WorkflowAuthorizationDeclaration Declaration(string capability) => new([
        new ExecutionRequirement("workflow.capability", 1, capability, "resource", null)]);

    private static ExecutionAuthorizationResult Approval(ExecutionAuthorizationContext context,
        string provider, string approval) => new(ExecutionAuthorizationDecision.ApprovalRequired,
        context.AuthorizationRequestId, provider, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow,
        approvalRequestId: approval);

    private static ExecutionAuthorizationResult Result(ExecutionAuthorizationDecision decision,
        ExecutionAuthorizationContext context, string provider, DateTimeOffset? expiresAt = null) => new(decision,
        context.AuthorizationRequestId, provider, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, expiresAt);

    private static WorkflowAuthorizationWake Wake(WorkflowAuthorizationPending pending, string approvalId) => new()
    {
        WorkflowRunId = pending.WorkflowRunId,
        AuthorizationRequestId = pending.Context.AuthorizationRequestId,
        ApprovalRequestId = approvalId,
        ProviderId = "policy",
        BindingId = pending.BindingId,
        ContextHash = pending.ContextHash,
        Now = DateTimeOffset.UtcNow
    };

    private sealed class RecordingAuthorizer(
        Func<ExecutionAuthorizationContext, string, int, ExecutionAuthorizationResult> evaluate)
        : IExecutionAuthorizer
    {
        public List<ExecutionAuthorizationContext> Requests { get; } = [];

        public ValueTask<ExecutionAuthorizationResult> AuthorizeAsync(
            ExecutionAuthorizationContext context, CancellationToken cancellationToken = default)
        {
            Requests.Add(context);
            return ValueTask.FromResult(evaluate(context, "policy", Requests.Count));
        }
    }

    private sealed class ProtectedWorkflow : IWorkflow<string, string>
    {
        private readonly Action? onCallback;
        private readonly string capability;
        public ProtectedWorkflow(Action? onCallback = null, string capability = "execute")
        {
            this.onCallback = onCallback;
            this.capability = capability;
        }
        public int Calls { get; private set; }

        public Task<string> RunAsync(WorkflowContext context, string input, CancellationToken cancellationToken) =>
            context.StepAsync("protected", input, (value, _) =>
            {
                Calls++;
                onCallback?.Invoke();
                return Task.FromResult(value);
            }, new StepOptions { Authorization = Declaration(capability) }, cancellationToken);
    }

    private sealed class MutableDeclarationWorkflow : IWorkflow<string, string>
    {
        public WorkflowAuthorizationDeclaration Authorization { get; set; } = Declaration("read");
        public int Calls { get; private set; }

        public Task<string> RunAsync(WorkflowContext context, string input, CancellationToken cancellationToken) =>
            context.StepAsync("protected", input, (value, _) =>
            {
                Calls++;
                return Task.FromResult(value);
            }, new StepOptions { Authorization = Authorization }, cancellationToken);
    }

    private sealed class ProtectedThenTailWorkflow : IWorkflow<string, string>
    {
        public int ProtectedCalls { get; private set; }
        public int TailCalls { get; private set; }

        public async Task<string> RunAsync(WorkflowContext context, string input, CancellationToken cancellationToken)
        {
            var output = await context.StepAsync("protected", input, (value, _) =>
            {
                ProtectedCalls++;
                return Task.FromResult(value);
            }, new StepOptions { Authorization = Declaration("execute") }, cancellationToken);
            return await context.StepAsync("tail", output, (value, _) =>
            {
                TailCalls++;
                return Task.FromResult(value);
            }, cancellationToken: cancellationToken);
        }
    }

    private sealed class ChildParentWorkflow : IWorkflow<string, string>
    {
        public Task<string> RunAsync(WorkflowContext context, string input, CancellationToken cancellationToken) =>
            context.StartChildAsync<string, string>(
                "child",
                "child",
                "1",
                input,
                new ChildRunOptions
                {
                    StartAuthorization = Declaration("child.start"),
                    WaitAuthorization = Declaration("child.wait")
                },
                cancellationToken);
    }

    private sealed class WriterLockAuthorizer(string databasePath) : IExecutionAuthorizer
    {
        private SqliteConnection? connection;
        private SqliteTransaction? transaction;
        public TaskCompletionSource LockAcquired { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<ExecutionAuthorizationResult> AuthorizeAsync(
            ExecutionAuthorizationContext context, CancellationToken cancellationToken = default)
        {
            connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Pooling = false,
                DefaultTimeout = 5
            }.ToString());
            connection.Open();
            transaction = connection.BeginTransaction(deferred: false);
            LockAcquired.TrySetResult();
            return ValueTask.FromResult(new ExecutionAuthorizationResult(
                ExecutionAuthorizationDecision.Allowed,
                context.AuthorizationRequestId,
                "policy",
                Guid.NewGuid().ToString("N"),
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow.AddMilliseconds(200)));
        }

        public void ReleaseWriterLock()
        {
            transaction?.Dispose();
            connection?.Dispose();
        }
    }
}
