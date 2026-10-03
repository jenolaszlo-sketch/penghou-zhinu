using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Penghou.Workflow.Abstractions;
using Penghou.Zhinu.Sqlite;

namespace Penghou.Zhinu.Hosting.Tests;

public sealed class ExecutionAuthorizationRegistrationTests
{
    [Fact]
    public void RegistrationAfterAddZhinu_IsAppliedAtResolution()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IWorkflowStore>(_ => null!);
        services.AddZhinu();
        var authorizer = new TestAuthorizer();

        services.AddZhinuExecutionAuthorization("policy", "host-v1", authorizer);

        var options = services.BuildServiceProvider().GetRequiredService<ZhinuOptions>();
        options.ExecutionAuthorization.Should().NotBeNull();
        options.ExecutionAuthorization!.ProviderId.Should().Be("policy");
        options.ExecutionAuthorization.BindingId.Should().Be("host-v1");
        options.ExecutionAuthorization.Authorizer.Should().BeSameAs(authorizer);
    }

    [Fact]
    public void DuplicateProviders_AreRejectedAtRegistration()
    {
        var services = new ServiceCollection();
        services.AddZhinuExecutionAuthorization("one", "host", new TestAuthorizer());

        var act = () => services.AddZhinuExecutionAuthorization("two", "host", new TestAuthorizer());

        act.Should().Throw<InvalidOperationException>().WithMessage("*exactly one provider*");
    }

    [Fact]
    public void RegistrationBeforeAddZhinu_IsAppliedAtResolution()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IWorkflowStore>(_ => null!);
        services.AddZhinuExecutionAuthorization("policy", "host-v1", new TestAuthorizer());
        services.AddZhinu();

        var options = services.BuildServiceProvider().GetRequiredService<ZhinuOptions>();

        options.ExecutionAuthorization.Should().NotBeNull();
    }

    [Fact]
    public async Task OptionsAddedAfterAuthorization_CannotShadowConfiguredProvider()
    {
        var services = new ServiceCollection();
        var storePath = Path.Combine(Path.GetTempPath(), $"zhinu-auth-{Guid.NewGuid():N}.db");
        var store = new SqliteWorkflowStore(new ZhinuSqliteOptions { DatabasePath = storePath, Pooling = false });
        services.AddSingleton<IWorkflowStore>(store);
        services.AddZhinuExecutionAuthorization("policy", "host-v1", new TestAuthorizer());
        services.AddZhinu();
        services.AddSingleton(new ZhinuOptions());
        var provider = services.BuildServiceProvider();

        var act = () => provider.GetRequiredService<WorkflowEngine>();

        act.Should().Throw<InvalidOperationException>().WithMessage("*exactly one effective ZhinuOptions*");
        await provider.DisposeAsync();
        SqliteConnection.ClearAllPools();
        if (File.Exists(storePath)) File.Delete(storePath);
    }

    [Fact]
    public async Task HostedDenial_PreventsClassStepAndScopedDependencyActivation()
    {
        var services = new ServiceCollection();
        var root = Path.Combine(Path.GetTempPath(), $"zhinu-auth-hosted-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var probe = new ActivationProbe();
        var key = new StepImplementationKey("protected-class-step");
        services.AddLogging();
        services.AddSingleton(probe);
        services.AddScoped<ActivationDependency>(_ =>
        {
            Interlocked.Increment(ref probe.DependencyActivations);
            return new ActivationDependency();
        });
        services.AddZhinuSqlite(options => options.DatabasePath = Path.Combine(root, "workflow.db"));
        services.AddZhinuExecutionAuthorization("policy", "host-v1", new TestAuthorizer((context, provider) =>
            new ExecutionAuthorizationResult(ExecutionAuthorizationDecision.Denied,
                context.AuthorizationRequestId, provider, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow)));
        services.AddZhinuWorkflow<ProtectedClassWorkflow, string, string>("protected-class", "1");
        services.AddZhinuStep<ProtectedClassStep, string, string>(key);
        services.AddZhinu();

        await using (var provider = services.BuildServiceProvider())
        {
            var engine = provider.GetRequiredService<WorkflowEngine>();
            var runId = await engine.StartAsync("protected-class", "1", "input", cancellationToken: TestContext.Current.CancellationToken);
            await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
            probe.StepConstructions.Should().Be(0);
            probe.DependencyActivations.Should().Be(0);
        }
        SqliteConnection.ClearAllPools();
        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public void ZhinuOptionsCallback_CannotCompeteWithExplicitRegistration()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IWorkflowStore>(_ => null!);
        var authorization = new WorkflowExecutionAuthorizationOptions("policy", "host", new TestAuthorizer());
        var act = () => services.AddZhinu(options => options.ExecutionAuthorization = authorization);

        act.Should().Throw<InvalidOperationException>().WithMessage("*AddZhinuExecutionAuthorization*");
    }

    private sealed class TestAuthorizer(
        Func<ExecutionAuthorizationContext, string, ExecutionAuthorizationResult>? evaluate = null) : IExecutionAuthorizer
    {
        private readonly Func<ExecutionAuthorizationContext, string, ExecutionAuthorizationResult> evaluate =
            evaluate ?? ((_, _) => throw new NotSupportedException());

        public ValueTask<ExecutionAuthorizationResult> AuthorizeAsync(
            ExecutionAuthorizationContext context,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(evaluate(context, "policy"));
    }

    private sealed class ProtectedClassWorkflow : IWorkflow<string, string>
    {
        private static readonly StepImplementationKey Key = new("protected-class-step");

        public Task<string> RunAsync(WorkflowContext context, string input, CancellationToken cancellationToken) =>
            context.StepAsync<string, string>("protected", Key, input,
                new StepOptions
                {
                    Authorization = new WorkflowAuthorizationDeclaration([
                        new ExecutionRequirement("workflow.capability", 1, "execute", "protected-class", null)])
                }, cancellationToken);
    }

    private sealed class ProtectedClassStep : WorkflowStep<string, string>
    {
        public ProtectedClassStep(ActivationProbe probe, ActivationDependency dependency)
        {
            _ = dependency;
            Interlocked.Increment(ref probe.StepConstructions);
        }

        public override Task<string> ExecuteAsync(WorkflowStepContext context, string input,
            CancellationToken cancellationToken) => Task.FromResult(input);
    }

    private sealed class ActivationProbe
    {
        public int StepConstructions;
        public int DependencyActivations;
    }

    private sealed class ActivationDependency { }
}
