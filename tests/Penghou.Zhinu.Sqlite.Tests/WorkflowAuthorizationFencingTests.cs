using FluentAssertions;
using Microsoft.Data.Sqlite;
using Penghou.Workflow.Abstractions;

namespace Penghou.Zhinu.Sqlite.Tests;

public sealed class WorkflowAuthorizationFencingTests : WorkflowEngineTestBase
{
    [Theory]
    [InlineData("null")]
    [InlineData("provider")]
    [InlineData("future")]
    [InlineData("lifetime")]
    public async Task InvalidProviderOutcomeFailsClosed(string mode)
    {
        var store = CreateStore();
        var workflow = new ProtectedWorkflow();
        await using var engine = Engine(store, workflow, new InvalidProvider(mode));
        var id = await engine.StartAsync("protected", "1", "input", cancellationToken: TestContext.Current.CancellationToken);
        await engine.ExecuteAsync(id, TestContext.Current.CancellationToken);
        workflow.Calls.Should().Be(0);
        (await engine.GetRunAsync(id, TestContext.Current.CancellationToken))!.Status.Should().Be(WorkflowStatus.Failed);
    }

    [Fact]
    public async Task ProviderIgnoringCancellationCannotOutliveBoundedPreflight()
    {
        var store = CreateStore();
        var workflow = new ProtectedWorkflow();
        var provider = new DeferredProvider();
        await using var engine = new WorkflowEngine(store,
            new WorkflowRegistry().Register("protected", "1", workflow), new ZhinuOptions
            {
                ExecutionAuthorization = new WorkflowExecutionAuthorizationOptions("policy", "host-v1", provider,
                    evaluationTimeout: TimeSpan.FromMilliseconds(50))
            });
        var id = await engine.StartAsync("protected", "1", "input", cancellationToken: TestContext.Current.CancellationToken);
        await engine.ExecuteAsync(id, TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        workflow.Calls.Should().Be(0);
        (await engine.GetRunAsync(id, TestContext.Current.CancellationToken))!.Status.Should().Be(WorkflowStatus.Failed);
        provider.Response.SetResult(Allow(await provider.Entered.Task));
    }
    [Theory]
    [InlineData("lease")]
    [InlineData("generation")]
    [InlineData("revision")]
    [InlineData("evidence")]
    public async Task ChangesDuringProviderAwaitPreventDispatch(string change)
    {
        var store = CreateStore();
        var provider = new DeferredProvider();
        var workflow = new ProtectedWorkflow();
        await using var engine = Engine(store, workflow, provider);
        var id = await engine.StartAsync("protected", "1", "input", cancellationToken: TestContext.Current.CancellationToken);
        var execution = engine.ExecuteAsync(id, TestContext.Current.CancellationToken);
        var context = await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(root, "zhinu.db"), Pooling = false }.ToString()))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = change switch
            {
                "lease" => "UPDATE workflow_steps SET lease_owner='other-host';",
                "generation" => "UPDATE workflow_runs SET lease_generation=lease_generation+1;",
                "revision" => "UPDATE workflow_steps SET revision=revision+1;",
                _ => "CREATE TRIGGER reject_authorization_evidence BEFORE INSERT ON workflow_authorization_evidence BEGIN SELECT RAISE(ABORT,'evidence unavailable'); END;"
            };
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
        provider.Response.SetResult(Allow(context));
        await execution.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        workflow.Calls.Should().Be(0);
        (await engine.GetRunAsync(id, TestContext.Current.CancellationToken))!.Status.Should().NotBe(WorkflowStatus.Completed);
    }

    [Fact]
    public async Task CallerCancellationDoesNotBecomeProviderFailureOrDispatch()
    {
        var store = CreateStore();
        var provider = new DeferredProvider();
        var workflow = new ProtectedWorkflow();
        await using var engine = Engine(store, workflow, provider);
        var id = await engine.StartAsync("protected", "1", "input", cancellationToken: TestContext.Current.CancellationToken);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var execution = engine.ExecuteAsync(id, cancellation.Token);
        var context = await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();
        await execution.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        provider.Response.SetResult(Allow(context));
        workflow.Calls.Should().Be(0);
        var run = (await engine.GetRunAsync(id, TestContext.Current.CancellationToken))!;
        run.Status.Should().Be(WorkflowStatus.Running);
        run.LeaseOwner.Should().BeNull();
        run.Error.Should().BeNull();
    }

    [Fact]
    public async Task SwallowedDenialCannotBecomeRunSuccess()
    {
        var store = CreateStore();
        var workflow = new ProtectedWorkflow { Swallow = true };
        await using var engine = Engine(store, workflow, new DenyProvider());
        var id = await engine.StartAsync("protected", "1", "input", cancellationToken: TestContext.Current.CancellationToken);
        await engine.ExecuteAsync(id, TestContext.Current.CancellationToken);
        workflow.Calls.Should().Be(0);
        (await engine.GetRunAsync(id, TestContext.Current.CancellationToken))!.Status.Should().Be(WorkflowStatus.Failed);
    }

    [Fact]
    public async Task RemovingRequiredEvidenceFromHostProfileCannotResumeRun()
    {
        var store = CreateStore();
        var workflow = new ProtectedWorkflow();
        Guid id;
        await using (var original = Engine(store, workflow, new DenyProvider(), new EvidenceVerifier()))
            id = await original.StartAsync("protected", "1", "input", cancellationToken: TestContext.Current.CancellationToken);
        await using var changedHost = Engine(store, workflow, new DenyProvider());
        await changedHost.ExecuteAsync(id, TestContext.Current.CancellationToken);
        workflow.Calls.Should().Be(0);
        (await changedHost.GetRunAsync(id, TestContext.Current.CancellationToken))!.Status.Should().Be(WorkflowStatus.Failed);
    }

    private static WorkflowEngine Engine(SqliteWorkflowStore store, ProtectedWorkflow workflow,
        IExecutionAuthorizer provider, IWorkflowAuthorizationEvidenceVerifier? evidence = null) =>
        new(store, new WorkflowRegistry().Register("protected", "1", workflow), new ZhinuOptions
        {
            ExecutionAuthorization = new WorkflowExecutionAuthorizationOptions("policy", "host-v1", provider, evidence)
        });

    private static ExecutionAuthorizationResult Allow(ExecutionAuthorizationContext context) =>
        new(ExecutionAuthorizationDecision.Allowed, context.AuthorizationRequestId, "policy", Guid.NewGuid().ToString("N"),
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1));

    private sealed class ProtectedWorkflow : IWorkflow<string, string>
    {
        internal int Calls;
        internal bool Swallow;
        public async Task<string> RunAsync(WorkflowContext context, string input, CancellationToken cancellationToken)
        {
            try
            {
                return await context.StepAsync("operation", input, (value, _) =>
                { Calls++; return Task.FromResult(value); }, new StepOptions
                { Authorization = new WorkflowAuthorizationDeclaration([new ExecutionRequirement("test", 1, "execute", "operation")]) }, cancellationToken);
            }
            catch (WorkflowAuthorizationException) when (Swallow) { return "swallowed"; }
        }
    }

    private sealed class DeferredProvider : IExecutionAuthorizer
    {
        internal readonly TaskCompletionSource<ExecutionAuthorizationContext> Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<ExecutionAuthorizationResult> Response = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<ExecutionAuthorizationResult> AuthorizeAsync(ExecutionAuthorizationContext context, CancellationToken cancellationToken = default)
        { Entered.TrySetResult(context); return new(Response.Task); }
    }
    private sealed class DenyProvider : IExecutionAuthorizer
    {
        public ValueTask<ExecutionAuthorizationResult> AuthorizeAsync(ExecutionAuthorizationContext context, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ExecutionAuthorizationResult(ExecutionAuthorizationDecision.Denied, context.AuthorizationRequestId,
                "policy", Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow));
    }
    private sealed class InvalidProvider(string mode) : IExecutionAuthorizer
    {
        public ValueTask<ExecutionAuthorizationResult> AuthorizeAsync(ExecutionAuthorizationContext context,
            CancellationToken cancellationToken = default)
        {
            if (mode == "null") return ValueTask.FromResult<ExecutionAuthorizationResult>(null!);
            var evaluated = mode == "future" ? DateTimeOffset.UtcNow.AddMinutes(1) : DateTimeOffset.UtcNow;
            return ValueTask.FromResult(new ExecutionAuthorizationResult(ExecutionAuthorizationDecision.Allowed,
                context.AuthorizationRequestId, mode == "provider" ? "wrong-provider" : "policy", Guid.NewGuid().ToString("N"),
                evaluated, evaluated.AddMinutes(mode == "lifetime" ? 60 : 1)));
        }
    }
    private sealed class EvidenceVerifier : IWorkflowAuthorizationEvidenceVerifier
    {
        public ValueTask<bool> VerifyAsync(ExecutionAuthorizationContext context, ExecutionAuthorizationResult result,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(true);
    }
}
