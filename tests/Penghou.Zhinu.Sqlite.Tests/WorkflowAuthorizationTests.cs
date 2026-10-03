using FluentAssertions;
using Penghou.Workflow.Abstractions;

namespace Penghou.Zhinu.Sqlite.Tests;

public sealed class WorkflowAuthorizationTests : WorkflowEngineTestBase
{
    [Theory]
    [InlineData(ExecutionAuthorizationDecision.Denied, false)]
    [InlineData(ExecutionAuthorizationDecision.Allowed, true)]
    public async Task ProviderDecision_ControlsProtectedCallback(ExecutionAuthorizationDecision decision, bool shouldRun)
    {
        var authorizer = new TestAuthorizer((context, provider) => Result(
            decision, context, provider,
            expiresAt: decision == ExecutionAuthorizationDecision.Allowed
                ? DateTimeOffset.UtcNow.AddMinutes(1) : null));
        var (engine, workflow, store) = CreateAuthorizedEngine(authorizer);
        await using var engineLifetime = engine;
        var runId = await engine.StartAsync("authorized", "1", "input", cancellationToken: TestContext.Current.CancellationToken);

        await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);

        workflow.Calls.Should().Be(shouldRun ? 1 : 0);
    }

    [Fact]
    public async Task BuilderOptionsClone_PreservesExplicitAuthority()
    {
        var authorizer = new TestAuthorizer((context, provider) => Result(
            ExecutionAuthorizationDecision.Allowed, context, provider,
            expiresAt: DateTimeOffset.UtcNow.AddMinutes(1)));
        var store = CreateStore();
        var workflow = new AuthorizationWorkflow();
        var registry = new WorkflowRegistry().Register("authorized", "1", workflow);
        var configured = new ZhinuOptions
        {
            LeaseDuration = TimeSpan.FromSeconds(2),
            LeaseRenewalInterval = TimeSpan.FromMilliseconds(500),
            ExecutionAuthorization = new WorkflowExecutionAuthorizationOptions("policy", "host-v1", authorizer)
        };
        await using var engine = new WorkflowEngineBuilder()
            .WithStore(store)
            .WithRegistry(registry)
            .WithOptions(configured)
            .Build();
        var runId = await engine.StartAsync("authorized", "1", "input", cancellationToken: TestContext.Current.CancellationToken);

        await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);

        workflow.Calls.Should().Be(1);
        authorizer.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task ExpiredDecision_IsRejectedBeforeCallback()
    {
        var authorizer = new TestAuthorizer((context, provider) =>
        {
            var evaluated = DateTimeOffset.UtcNow.AddMinutes(-2);
            return new ExecutionAuthorizationResult(ExecutionAuthorizationDecision.Allowed,
                context.AuthorizationRequestId, provider, Guid.NewGuid().ToString("N"), evaluated,
                evaluated.AddMinutes(1));
        });
        var (engine, workflow, store) = CreateAuthorizedEngine(authorizer);
        await using var engineLifetime = engine;
        var runId = await engine.StartAsync("authorized", "1", "input", cancellationToken: TestContext.Current.CancellationToken);

        await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);

        workflow.Calls.Should().Be(0);
    }

    [Theory]
    [InlineData("mismatch")]
    [InlineData("throw")]
    public async Task InvalidOrThrowingProvider_IsRejectedBeforeCallback(string mode)
    {
        var authorizer = new TestAuthorizer((context, provider) => mode == "throw"
            ? throw new InvalidOperationException("provider failure")
            : Result(ExecutionAuthorizationDecision.Allowed, context, provider,
                requestId: "wrong-request", expiresAt: DateTimeOffset.UtcNow.AddMinutes(1)));
        var (engine, workflow, store) = CreateAuthorizedEngine(authorizer);
        await using var engineLifetime = engine;
        var runId = await engine.StartAsync("authorized", "1", "input", cancellationToken: TestContext.Current.CancellationToken);

        await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);

        workflow.Calls.Should().Be(0);
    }

    [Fact]
    public async Task ProviderFailureDoesNotConsumeCallbackRetryAllowance()
    {
        var authorizer = new TestAuthorizer((_, _) => throw new InvalidOperationException("unavailable"));
        var (engine, workflow, store) = CreateAuthorizedEngine(authorizer);
        await using var engineLifetime = engine;
        var runId = await engine.StartAsync("authorized", "1", "input", cancellationToken: TestContext.Current.CancellationToken);

        await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);

        workflow.Calls.Should().Be(0);
        authorizer.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task AuthorizationIsRecheckedForActualRetryAttempt()
    {
        var evaluations = 0;
        var authorizer = new TestAuthorizer((context, provider) => ++evaluations == 1
            ? Result(ExecutionAuthorizationDecision.Allowed, context, provider,
                expiresAt: DateTimeOffset.UtcNow.AddMinutes(1))
            : Result(ExecutionAuthorizationDecision.Denied, context, provider));
        var store = CreateStore();
        var workflow = new AuthorizationWorkflow(failFirstAttempt: true);
        var registry = new WorkflowRegistry().Register("authorized", "1", workflow);
        var options = new ZhinuOptions
        {
            LeaseDuration = TimeSpan.FromSeconds(2),
            LeaseRenewalInterval = TimeSpan.FromMilliseconds(500),
            ExecutionAuthorization = new WorkflowExecutionAuthorizationOptions("policy", "host-v1", authorizer)
        };
        await using var engine = new WorkflowEngine(store, registry, options);
        var runId = await engine.StartAsync("authorized", "1", "input", cancellationToken: TestContext.Current.CancellationToken);

        await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);

        if (authorizer.Requests.Count == 1)
            await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);

        workflow.Calls.Should().Be(1);
        authorizer.Requests.Should().HaveCount(2);
        authorizer.Requests.Select(request => request.Identity.Attempt).Should().Equal(1, 2);
        authorizer.Requests.Select(request => request.AuthorizationRequestId).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task RejectedRequiredEvidence_BlocksDispatch()
    {
        var authorizer = new TestAuthorizer((context, provider) => Result(
            ExecutionAuthorizationDecision.Allowed, context, provider,
            expiresAt: DateTimeOffset.UtcNow.AddMinutes(1), evidenceId: "evidence-ref"));
        var verifier = new RejectEvidenceVerifier();
        var (engine, workflow, store) = CreateAuthorizedEngine(authorizer, verifier);
        await using var engineLifetime = engine;
        var runId = await engine.StartAsync("authorized", "1", "input", cancellationToken: TestContext.Current.CancellationToken);

        await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);

        workflow.Calls.Should().Be(0);
        verifier.Calls.Should().Be(1);
    }

    [Fact]
    public async Task CompensationRequiresItsOwnAuthorizationDecision()
    {
        var authorizer = new TestAuthorizer((context, provider) =>
        {
            var decision = context.Identity.OperationId.StartsWith("compensation:", StringComparison.Ordinal)
                ? ExecutionAuthorizationDecision.Denied
                : ExecutionAuthorizationDecision.Allowed;
            return Result(decision, context, provider,
                expiresAt: decision == ExecutionAuthorizationDecision.Allowed
                    ? DateTimeOffset.UtcNow.AddMinutes(1) : null);
        });
        var store = CreateStore();
        var workflow = new CompensationAuthorizationWorkflow();
        var registry = new WorkflowRegistry().Register("compensation", "1", workflow);
        var options = new ZhinuOptions
        {
            LeaseDuration = TimeSpan.FromSeconds(2),
            LeaseRenewalInterval = TimeSpan.FromMilliseconds(500),
            ExecutionAuthorization = new WorkflowExecutionAuthorizationOptions("policy", "host-v1", authorizer)
        };
        await using var engine = new WorkflowEngine(store, registry, options);
        await engine.RunAsync<string, string>("compensation", "1", "input", cancellationToken: TestContext.Current.CancellationToken);
        Func<Task> rollback = () => engine.RollbackAsync(workflow.RunId, cancellationToken: TestContext.Current.CancellationToken);
        await rollback.Should().ThrowAsync<WorkflowAuthorizationException>();

        workflow.CompensationCalls.Should().Be(0);
        authorizer.Requests.Should().HaveCount(2);
        authorizer.Requests.Last().Identity.OperationId.Should().StartWith("compensation:");
    }

    [Fact]
    public async Task ProtectedRunCannotResumeWithoutItsProviderBinding()
    {
        var authorizer = new TestAuthorizer((context, provider) => Result(
            ExecutionAuthorizationDecision.Allowed, context, provider,
            expiresAt: DateTimeOffset.UtcNow.AddMinutes(1)));
        var store = CreateStore();
        var workflow = new AuthorizationWorkflow();
        var registry = new WorkflowRegistry().Register("authorized", "1", workflow);
        var options = new ZhinuOptions
        {
            LeaseDuration = TimeSpan.FromSeconds(2),
            LeaseRenewalInterval = TimeSpan.FromMilliseconds(500),
            ExecutionAuthorization = new WorkflowExecutionAuthorizationOptions("policy", "host-v1", authorizer)
        };
        await using var protectedEngine = new WorkflowEngine(store, registry, options);
        var runId = await protectedEngine.StartAsync("authorized", "1", "input", cancellationToken: TestContext.Current.CancellationToken);
        await using var unprotectedEngine = new WorkflowEngine(store, registry, new ZhinuOptions());
        await unprotectedEngine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
        var failedRun = await unprotectedEngine.GetRunAsync(runId, TestContext.Current.CancellationToken);

        failedRun!.Status.Should().Be(WorkflowStatus.Failed);
        failedRun.Error!.Type.Should().Be(typeof(WorkflowAuthorizationException).FullName);
        workflow.Calls.Should().Be(0);
    }

    [Fact]
    public async Task ApprovalWakeAfterEngineRestart_UsesFreshRequestBeforeDispatch()
    {
        var calls = 0;
        ExecutionAuthorizationResult AuthorizerCall(ExecutionAuthorizationContext context, string provider)
        {
            calls++;
            return calls == 1
                ? new ExecutionAuthorizationResult(ExecutionAuthorizationDecision.ApprovalRequired,
                    context.AuthorizationRequestId, provider, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow,
                    approvalRequestId: "approval-correlation")
                : Result(ExecutionAuthorizationDecision.Allowed, context, provider,
                    expiresAt: DateTimeOffset.UtcNow.AddMinutes(1));
        }
        var authorizer = new TestAuthorizer((context, provider) => AuthorizerCall(context, provider));

        var store = CreateStore();
        var workflow = new AuthorizationWorkflow();
        var registry = new WorkflowRegistry().Register("authorized", "1", workflow);
        var options = new ZhinuOptions
        {
            LeaseDuration = TimeSpan.FromSeconds(2),
            LeaseRenewalInterval = TimeSpan.FromMilliseconds(500),
            ExecutionAuthorization = new WorkflowExecutionAuthorizationOptions("policy", "host-v1", authorizer)
        };
        var firstEngine = new WorkflowEngine(store, registry, options);
        var runId = await firstEngine.StartAsync("authorized", "1", "input", cancellationToken: TestContext.Current.CancellationToken);
        await firstEngine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
        var step = (await firstEngine.GetStepsAsync(runId, TestContext.Current.CancellationToken)).Single();
        var pending = await store.GetPendingAuthorizationAsync(runId, step.Id, false, TestContext.Current.CancellationToken);
        pending.Should().NotBeNull();
        pending!.Ready.Should().BeFalse();
        var firstRequest = pending.Context.AuthorizationRequestId;
        await firstEngine.DisposeAsync();
        await using var resumedEngine = new WorkflowEngine(store, registry, options);
        var woke = await store.WakeAuthorizationAsync(new WorkflowAuthorizationWake
        {
            WorkflowRunId = runId,
            AuthorizationRequestId = firstRequest,
            ApprovalRequestId = "approval-correlation",
            ProviderId = "policy",
            BindingId = pending.BindingId,
            ContextHash = pending.ContextHash,
            Now = DateTimeOffset.UtcNow
        }, TestContext.Current.CancellationToken);
        woke.Should().BeTrue();
        await resumedEngine.ExecuteAsync(runId, TestContext.Current.CancellationToken);

        workflow.Calls.Should().Be(1);
        authorizer.Requests.Should().HaveCount(2);
        authorizer.Requests[1].AuthorizationRequestId.Should().NotBe(firstRequest);
    }

    [Fact]
    public async Task CompensationApproval_WakesAfterRestartAndReauthorizesAttemptOne()
    {
        var compensationEvaluations = 0;
        var authorizer = new TestAuthorizer((context, provider) =>
        {
            if (context.Identity.OperationId.StartsWith("compensation:", StringComparison.Ordinal) &&
                Interlocked.Increment(ref compensationEvaluations) == 1)
            {
                return new ExecutionAuthorizationResult(ExecutionAuthorizationDecision.ApprovalRequired,
                    context.AuthorizationRequestId, provider, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow,
                    approvalRequestId: "compensation-approval");
            }
            return Result(ExecutionAuthorizationDecision.Allowed, context, provider,
                expiresAt: DateTimeOffset.UtcNow.AddMinutes(1));
        });
        var store = CreateStore();
        var workflow = new CompensationAuthorizationWorkflow();
        var registry = new WorkflowRegistry().Register("compensation", "1", workflow);
        var options = AuthorizedOptions(authorizer);
        var firstEngine = new WorkflowEngine(store, registry, options);
        await firstEngine.RunAsync<string, string>("compensation", "1", "input", cancellationToken: TestContext.Current.CancellationToken);
        await firstEngine.RollbackAsync(workflow.RunId, cancellationToken: TestContext.Current.CancellationToken);

        var pausedRun = await firstEngine.GetRunAsync(workflow.RunId, TestContext.Current.CancellationToken);
        pausedRun!.Status.Should().Be(WorkflowStatus.Completed);
        workflow.CompensationCalls.Should().Be(0);
        var compensation = (await firstEngine.GetCompensationsAsync(workflow.RunId, TestContext.Current.CancellationToken)).Single();
        compensation.Status.Should().Be(CompensationStatus.Pending);
        var pending = await store.GetPendingAuthorizationAsync(workflow.RunId, compensation.Id, true,
            TestContext.Current.CancellationToken);
        pending.Should().NotBeNull();
        pending!.Ready.Should().BeFalse();
        var oldRequestId = pending.Context.AuthorizationRequestId;
        await firstEngine.DisposeAsync();

        await using var resumedEngine = new WorkflowEngine(store, registry, options);
        (await store.WakeAuthorizationAsync(CreateWake(workflow.RunId, pending), TestContext.Current.CancellationToken))
            .Should().BeTrue();
        await resumedEngine.RollbackAsync(workflow.RunId, cancellationToken: TestContext.Current.CancellationToken);

        workflow.CompensationCalls.Should().Be(1);
        var compRequests = authorizer.Requests.Where(request =>
            request.Identity.OperationId.StartsWith("compensation:", StringComparison.Ordinal)).ToArray();
        compRequests.Should().HaveCount(2);
        compRequests.Select(request => request.Identity.Attempt).Should().Equal(1, 1);
        compRequests.Select(request => request.AuthorizationRequestId).Should().OnlyHaveUniqueItems();
        compRequests[1].AuthorizationRequestId.Should().NotBe(oldRequestId);
        (await resumedEngine.GetRunAsync(workflow.RunId, TestContext.Current.CancellationToken))!.Status
            .Should().Be(WorkflowStatus.Compensated);
    }

    [Fact]
    public async Task CompensationApprovalDuringRollbackAndRestart_ResumesWithoutFailingTheOperation()
    {
        var compensationEvaluations = 0;
        var authorizer = new TestAuthorizer((context, provider) =>
        {
            if (context.Identity.OperationId.StartsWith("compensation:", StringComparison.Ordinal) &&
                Interlocked.Increment(ref compensationEvaluations) == 1)
            {
                return new ExecutionAuthorizationResult(ExecutionAuthorizationDecision.ApprovalRequired,
                    context.AuthorizationRequestId, provider, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow,
                    approvalRequestId: "restart-compensation-approval");
            }
            return Result(ExecutionAuthorizationDecision.Allowed, context, provider,
                expiresAt: DateTimeOffset.UtcNow.AddMinutes(1));
        });
        var store = CreateStore();
        var workflow = new CompensationAuthorizationWorkflow();
        var registry = new WorkflowRegistry().Register("compensation", "1", workflow);
        var options = AuthorizedOptions(authorizer);
        var firstEngine = new WorkflowEngine(store, registry, options);
        await firstEngine.RunAsync<string, string>("compensation", "1", "input", cancellationToken: TestContext.Current.CancellationToken);
        await firstEngine.RollbackAndRestartAsync(workflow.RunId, cancellationToken: TestContext.Current.CancellationToken);

        var parkedRun = await firstEngine.GetRunAsync(workflow.RunId, TestContext.Current.CancellationToken);
        parkedRun!.Status.Should().Be(WorkflowStatus.RollingBack);
        workflow.CompensationCalls.Should().Be(0);
        var compensation = (await firstEngine.GetCompensationsAsync(workflow.RunId, TestContext.Current.CancellationToken)).Single();
        var pending = await store.GetPendingAuthorizationAsync(workflow.RunId, compensation.Id, true,
            TestContext.Current.CancellationToken);
        pending.Should().NotBeNull();
        pending!.Ready.Should().BeFalse();
        var oldRequestId = pending.Context.AuthorizationRequestId;
        var operation = await store.GetActiveOperationAsync(workflow.RunId, TestContext.Current.CancellationToken);
        operation.Should().NotBeNull();
        await firstEngine.DisposeAsync();

        await using var resumedEngine = new WorkflowEngine(store, registry, options);
        (await store.WakeAuthorizationAsync(CreateWake(workflow.RunId, pending), TestContext.Current.CancellationToken))
            .Should().BeTrue();
        await resumedEngine.ExecuteAsync(workflow.RunId, TestContext.Current.CancellationToken);

        workflow.CompensationCalls.Should().Be(1);
        var compRequests = authorizer.Requests.Where(request =>
            request.Identity.OperationId.StartsWith("compensation:", StringComparison.Ordinal)).ToArray();
        compRequests.Should().HaveCount(2);
        compRequests.Select(request => request.Identity.Attempt).Should().Equal(1, 1);
        compRequests[1].AuthorizationRequestId.Should().NotBe(oldRequestId);
        (await resumedEngine.GetRunAsync(workflow.RunId, TestContext.Current.CancellationToken))!.Status
            .Should().Be(WorkflowStatus.Pending);
        (await store.GetActiveOperationAsync(workflow.RunId, TestContext.Current.CancellationToken)).Should().BeNull();

        await resumedEngine.ExecuteAsync(workflow.RunId, TestContext.Current.CancellationToken);
        (await resumedEngine.GetRunAsync(workflow.RunId, TestContext.Current.CancellationToken))!.Status
            .Should().Be(WorkflowStatus.Completed);
    }

    private static ZhinuOptions AuthorizedOptions(TestAuthorizer authorizer) => new()
    {
        LeaseDuration = TimeSpan.FromSeconds(2),
        LeaseRenewalInterval = TimeSpan.FromMilliseconds(500),
        ExecutionAuthorization = new WorkflowExecutionAuthorizationOptions("policy", "host-v1", authorizer)
    };

    private static WorkflowAuthorizationWake CreateWake(Guid runId, WorkflowAuthorizationPending pending) => new()
    {
        WorkflowRunId = runId,
        AuthorizationRequestId = pending.Context.AuthorizationRequestId,
        ApprovalRequestId = pending.Result.ApprovalRequestId!,
        ProviderId = pending.Result.ProviderId,
        BindingId = pending.BindingId,
        ContextHash = pending.ContextHash,
        Now = DateTimeOffset.UtcNow
    };

    private (WorkflowEngine Engine, AuthorizationWorkflow Workflow, SqliteWorkflowStore Store) CreateAuthorizedEngine(TestAuthorizer authorizer)
        => CreateAuthorizedEngine(authorizer, null);

    private (WorkflowEngine Engine, AuthorizationWorkflow Workflow, SqliteWorkflowStore Store) CreateAuthorizedEngine(
        TestAuthorizer authorizer, IWorkflowAuthorizationEvidenceVerifier? verifier)
    {
        var store = CreateStore();
        var workflow = new AuthorizationWorkflow();
        var registry = new WorkflowRegistry().Register("authorized", "1", workflow);
        var options = new ZhinuOptions
        {
            LeaseDuration = TimeSpan.FromSeconds(2),
            LeaseRenewalInterval = TimeSpan.FromMilliseconds(500),
            ExecutionAuthorization = new WorkflowExecutionAuthorizationOptions("policy", "host-v1", authorizer, verifier)
        };
        return (new WorkflowEngine(store, registry, options), workflow, store);
    }

    private static ExecutionAuthorizationResult Result(ExecutionAuthorizationDecision decision,
        ExecutionAuthorizationContext context, string provider, string? requestId = null,
        DateTimeOffset? expiresAt = null, string? evidenceId = null) => new(decision,
        requestId ?? context.AuthorizationRequestId, provider, Guid.NewGuid().ToString("N"),
        DateTimeOffset.UtcNow, expiresAt, evidenceId: evidenceId);

    private sealed class AuthorizationWorkflow : IWorkflow<string, string>
    {
        private readonly bool failFirstAttempt;

        public AuthorizationWorkflow(bool failFirstAttempt = false) => this.failFirstAttempt = failFirstAttempt;

        public int Calls { get; private set; }

        public async Task<string> RunAsync(WorkflowContext context, string input, CancellationToken cancellationToken)
        {
            return await context.StepAsync("protected", input, (value, _) =>
            {
                Calls++;
                if (failFirstAttempt && Calls == 1)
                    throw new InvalidOperationException("retry protected operation");
                return Task.FromResult(value);
            }, new StepOptions
            {
                Retry = new RetryPolicy { MaxAttempts = 3 },
                Authorization = new WorkflowAuthorizationDeclaration([
                    new ExecutionRequirement("workflow.capability", 1, "execute", "authorized", null)])
            }, cancellationToken);
        }
    }

    private sealed class CompensationAuthorizationWorkflow : IWorkflow<string, string>
    {
        public int CompensationCalls { get; private set; }
        public Guid RunId { get; private set; }

        public async Task<string> RunAsync(WorkflowContext context, string input, CancellationToken cancellationToken)
        {
            RunId = context.WorkflowRunId;
            return await context.StepAsync("first", input, (value, _, _) => Task.FromResult(value),
                new StepOptions
                {
                    Authorization = new WorkflowAuthorizationDeclaration([
                        new ExecutionRequirement("workflow.capability", 1, "execute", "forward", null)]),
                    CompensationAuthorization = new WorkflowAuthorizationDeclaration([
                        new ExecutionRequirement("workflow.capability", 1, "compensate", "forward", null)])
                }, cancellationToken,
                (_, _, _) =>
                {
                    CompensationCalls++;
                    return Task.CompletedTask;
                });
        }
    }

    private sealed class TestAuthorizer(
        Func<ExecutionAuthorizationContext, string, ExecutionAuthorizationResult> evaluate) : IExecutionAuthorizer
    {
        public List<ExecutionAuthorizationContext> Requests { get; } = [];

        public ValueTask<ExecutionAuthorizationResult> AuthorizeAsync(ExecutionAuthorizationContext context,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(context);
            return ValueTask.FromResult(evaluate(context, "policy"));
        }
    }

    private sealed class RejectEvidenceVerifier : IWorkflowAuthorizationEvidenceVerifier
    {
        public int Calls { get; private set; }

        public ValueTask<bool> VerifyAsync(ExecutionAuthorizationContext context,
            ExecutionAuthorizationResult result, CancellationToken cancellationToken = default)
        {
            Calls++;
            return ValueTask.FromResult(false);
        }
    }
}
