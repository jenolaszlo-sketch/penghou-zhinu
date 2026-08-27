using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Penghou.Zhinu.Sqlite;

namespace Penghou.Zhinu.Hosting.Tests;

public sealed class ClassBasedWorkflowStepTests : IDisposable
{
    private static readonly StepImplementationKey PlanningKey = new("planning");
    private static readonly StepImplementationKey ArchitectureKey = new("architecture");
    private static readonly StepImplementationKey ReservationKey = new("reservation");
    private static readonly StepImplementationKey DisposalKey = new("disposal");
    private static readonly StepImplementationKey CancellationKey = new("cancellation");
    private static readonly StepImplementationKey ExecutionOnlyKey = new("execution-only");
    private static readonly WorkflowStepReference<string, string> PlanningReference =
        new(PlanningKey);
    private static readonly WorkflowStepReference<string, string> EventReference =
        new(new("event-step"));

    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "penghou-zhinu-class-step-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task KeyedSteps_WithSameContracts_ResolveIndependently()
    {
        var services = CreateServices<TwoKeyWorkflow, string, string>("two-keys");
        services.AddZhinuStep<PlanningStep, string, string>(PlanningKey);
        services.AddKeyedScoped<IWorkflowStep<string, string>, ArchitectureStep>(
            ArchitectureKey);
        await using var provider = services.BuildServiceProvider();
        var engine = provider.GetRequiredService<WorkflowEngine>();

        var result = await engine.RunAsync<string, string>(
            "two-keys",
            "1",
            "request",
            cancellationToken: TestContext.Current.CancellationToken);

        result.Should().Be("plan:request|architecture:request");
        var steps = await engine.GetStepsAsync(
            provider.GetRequiredService<TwoKeyWorkflow>().RunId,
            TestContext.Current.CancellationToken);
        steps.Select(step => step.ImplementationKey)
            .Should().Equal(PlanningKey.Value, ArchitectureKey.Value);
    }

    [Fact]
    public async Task TypedReference_BindsRegistrationAndInfersInvocationContract()
    {
        var services = CreateServices<TypedReferenceWorkflow, string, string>(
            "typed-reference");
        services.AddZhinuStep<PlanningStep>(PlanningReference);
        await using var provider = services.BuildServiceProvider();

        var result = await provider.GetRequiredService<WorkflowEngine>()
            .RunAsync<string, string>(
                "typed-reference",
                "1",
                "request",
                cancellationToken: TestContext.Current.CancellationToken);

        result.Should().Be("plan:request");
    }

    [Fact]
    public void TypedReferenceRegistration_RejectsMismatchedImplementationContract()
    {
        var services = new ServiceCollection();
        var mismatched = new WorkflowStepReference<int, string>(PlanningKey);

        var action = () => services.AddZhinuStep<PlanningStep>(mismatched);

        action.Should().Throw<WorkflowRegistrationException>()
            .WithMessage("*does not implement*IWorkflowStep*");
    }

    [Fact]
    public async Task ClassStepFanOut_IsDurableAndPreservesInputOrder()
    {
        var services = CreateServices<TypedFanOutWorkflow, string, string>(
            "typed-fanout");
        services.AddZhinuStep<PlanningStep>(PlanningReference);
        await using var provider = services.BuildServiceProvider();
        var engine = provider.GetRequiredService<WorkflowEngine>();
        var workflow = provider.GetRequiredService<TypedFanOutWorkflow>();

        var result = await engine.RunAsync<string, string>(
            "typed-fanout",
            "1",
            "a,b,c",
            cancellationToken: TestContext.Current.CancellationToken);

        result.Should().Be("plan:a|plan:b|plan:c");
        var steps = await engine.GetStepsAsync(
            workflow.RunId,
            TestContext.Current.CancellationToken);
        steps.Select(step => step.StepKey)
            .Should().Equal("items.0", "items.1", "items.2");
        steps.Should().OnlyContain(step =>
            step.ImplementationKey == PlanningKey.Value);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task StepContextEvent_CommitsOnlyWithSuccessfulClassStep(
        bool fail,
        bool expected)
    {
        var services = CreateServices<EventWorkflow, string, string>(
            $"step-event-{fail}");
        services.AddSingleton(new EventProbe { Fail = fail });
        services.AddZhinuStep<EventEmittingStep>(EventReference);
        await using var provider = services.BuildServiceProvider();
        var engine = provider.GetRequiredService<WorkflowEngine>();
        var runId = await engine.StartAsync(
            $"step-event-{fail}",
            "1",
            "value",
            cancellationToken: TestContext.Current.CancellationToken);

        await engine.ExecuteAsync(
            runId,
            TestContext.Current.CancellationToken);
        var events = await engine.GetEventsAsync(
            runId,
            cancellationToken: TestContext.Current.CancellationToken);

        events.Any(@event => @event.EventType == "step-progress")
            .Should().Be(expected);
        if (expected)
        {
            var progress = events.Single(@event =>
                @event.EventType == "step-progress");
            progress.StepKey.Should().Be("event");
            progress.DataJson.Should().Contain("value");
        }
    }

    [Fact]
    public async Task Compensation_ReplaysWithoutResolving_AndUsesDurableInputInFreshScope()
    {
        var services = CreateServices<CompensatingWorkflow, string, string>("compensating");
        services.AddSingleton<CompensationProbe>();
        services.AddScoped<AttemptResource>();
        services.AddZhinuStep<ReservationStep, string, string>(ReservationKey);
        await using var provider = services.BuildServiceProvider();
        var engine = provider.GetRequiredService<WorkflowEngine>();
        var workflow = provider.GetRequiredService<CompensatingWorkflow>();

        var result = await engine.RunAsync<string, string>(
            "compensating",
            "1",
            "original",
            cancellationToken: TestContext.Current.CancellationToken);
        workflow.UseChangedReplayInput = true;

        await engine.RollbackAsync(
            workflow.RunId,
            cancellationToken: TestContext.Current.CancellationToken);

        result.Should().Be("reserved:original");
        var probe = provider.GetRequiredService<CompensationProbe>();
        probe.ExecutionInputs.Should().Equal("original");
        probe.CompensationInputs.Should().Equal("original");
        probe.CompensationOutputs.Should().Equal("reserved:original");
        probe.StepInstances.Should().HaveCount(2).And.OnlyHaveUniqueItems();
        probe.ResourceInstances.Should().HaveCount(2).And.OnlyHaveUniqueItems();
        probe.DisposedResources.Should().BeEquivalentTo(probe.ResourceInstances);
    }

    [Fact]
    public async Task DisposalFailure_PreventsCommit_AndRetryUsesFreshScope()
    {
        var services = CreateServices<DisposalWorkflow, string, string>("disposal");
        services.AddSingleton<DisposalProbe>();
        services.AddScoped<FailFirstDisposalResource>();
        services.AddZhinuStep<DisposalStep, string, string>(DisposalKey);
        await using var provider = services.BuildServiceProvider();
        var engine = provider.GetRequiredService<WorkflowEngine>();

        var result = await engine.RunAsync<string, string>(
            "disposal",
            "1",
            "value",
            cancellationToken: TestContext.Current.CancellationToken);

        result.Should().Be("VALUE");
        var probe = provider.GetRequiredService<DisposalProbe>();
        probe.ExecuteCount.Should().Be(2);
        probe.DisposeCount.Should().Be(2);
        probe.StepInstances.Should().HaveCount(2).And.OnlyHaveUniqueItems();
        probe.ResourceInstances.Should().HaveCount(2).And.OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task CompensationRetry_UsesFreshStepAndScopeForEveryAttempt()
    {
        var services = CreateServices<CompensatingWorkflow, string, string>("compensation-retry");
        services.AddSingleton<CompensationProbe>();
        services.AddScoped<AttemptResource>();
        services.AddZhinuStep<ReservationStep, string, string>(ReservationKey);
        await using var provider = services.BuildServiceProvider();
        var probe = provider.GetRequiredService<CompensationProbe>();
        probe.FailFirstCompensation = true;
        var engine = provider.GetRequiredService<WorkflowEngine>();
        var workflow = provider.GetRequiredService<CompensatingWorkflow>();

        await engine.RunAsync<string, string>(
            "compensation-retry",
            "1",
            "original",
            cancellationToken: TestContext.Current.CancellationToken);
        await engine.RollbackAsync(
            workflow.RunId,
            cancellationToken: TestContext.Current.CancellationToken);

        probe.CompensationAttempts.Should().Be(2);
        probe.StepInstances.Should().HaveCount(3).And.OnlyHaveUniqueItems();
        probe.ResourceInstances.Should().HaveCount(3).And.OnlyHaveUniqueItems();
        probe.DisposedResources.Should().BeEquivalentTo(probe.ResourceInstances);
    }

    [Fact]
    public async Task MissingImplementationKey_FailsWithResolutionDetails()
    {
        var services = CreateServices<MissingStepWorkflow, string, string>("missing-step");
        await using var provider = services.BuildServiceProvider();
        var engine = provider.GetRequiredService<WorkflowEngine>();

        var action = () => engine.RunAsync<string, string>(
            "missing-step",
            "1",
            "value",
            cancellationToken: TestContext.Current.CancellationToken);

        await action.Should().ThrowAsync<WorkflowExecutionFailedException>()
            .WithMessage("*Could not resolve workflow step 'planning'*");
    }

    [Fact]
    public async Task CompensationEnabled_ForExecutionOnlyStep_FailsBeforeExecution()
    {
        var services = CreateServices<ExecutionOnlyWorkflow, string, string>("execution-only");
        services.AddSingleton<ExecutionOnlyProbe>();
        services.AddZhinuStep<ExecutionOnlyStep, string, string>(ExecutionOnlyKey);
        await using var provider = services.BuildServiceProvider();
        var engine = provider.GetRequiredService<WorkflowEngine>();

        var action = () => engine.RunAsync<string, string>(
            "execution-only",
            "1",
            "value",
            cancellationToken: TestContext.Current.CancellationToken);

        await action.Should().ThrowAsync<WorkflowExecutionFailedException>()
            .WithMessage("*enabled compensation*does not implement*");
        provider.GetRequiredService<ExecutionOnlyProbe>().ExecutionCount.Should().Be(0);
    }

    [Fact]
    public void AddZhinuStep_RejectsDuplicateKeyAndContract()
    {
        var services = new ServiceCollection();
        services.AddZhinuStep<PlanningStep, string, string>(PlanningKey);

        var action = () => services.AddZhinuStep<ArchitectureStep, string, string>(PlanningKey);

        action.Should().Throw<WorkflowRegistrationException>()
            .WithMessage("*already registered*planning*");
    }

    [Fact]
    public async Task Resolver_RejectsDuplicateDirectKeyedRegistrations()
    {
        var services = CreateServices<MissingStepWorkflow, string, string>("duplicate-direct");
        services.AddKeyedScoped<IWorkflowStep<string, string>, PlanningStep>(PlanningKey);
        services.AddKeyedScoped<IWorkflowStep<string, string>, ArchitectureStep>(PlanningKey);
        await using var provider = services.BuildServiceProvider();
        var engine = provider.GetRequiredService<WorkflowEngine>();

        var action = () => engine.RunAsync<string, string>(
            "duplicate-direct",
            "1",
            "value",
            cancellationToken: TestContext.Current.CancellationToken);

        await action.Should().ThrowAsync<WorkflowExecutionFailedException>()
            .WithMessage("*Multiple workflow steps are registered*planning*");
    }

    [Fact]
    public async Task EngineBuilder_AcceptsProviderNeutralStepResolver()
    {
        var probe = new ExecutionOnlyProbe();
        var resolver = new FixedWorkflowStepResolver(
            ExecutionOnlyKey,
            new ExecutionOnlyStep(probe));
        var registry = new WorkflowRegistry();
        registry.Register(new WorkflowRegistration<string, string>(
            new WorkflowDefinition { Name = "custom-resolver", Version = "1" },
            () => new CustomResolverWorkflow()));
        var store = new SqliteWorkflowStore(new ZhinuSqliteOptions
        {
            DatabasePath = Path.Combine(root, "custom-resolver.db")
        });
        await using var engine = new WorkflowEngineBuilder()
            .WithStore(store)
            .WithRegistry(registry)
            .WithStepResolver(resolver)
            .Build();

        var result = await engine.RunAsync<string, string>(
            "custom-resolver",
            "1",
            "value",
            cancellationToken: TestContext.Current.CancellationToken);

        result.Should().Be("value");
        probe.ExecutionCount.Should().Be(1);
        resolver.DisposeCount.Should().Be(1);
    }

    [Fact]
    public async Task Rollback_WithChangedImplementationKey_IsRejected()
    {
        var services = CreateServices<MutableKeyWorkflow, string, string>("changed-key");
        services.AddZhinuStep<PlanningStep, string, string>(PlanningKey);
        services.AddZhinuStep<ArchitectureStep, string, string>(ArchitectureKey);
        await using var provider = services.BuildServiceProvider();
        var engine = provider.GetRequiredService<WorkflowEngine>();
        var workflow = provider.GetRequiredService<MutableKeyWorkflow>();

        await engine.RunAsync<string, string>(
            "changed-key",
            "1",
            "value",
            cancellationToken: TestContext.Current.CancellationToken);
        workflow.ImplementationKey = ArchitectureKey;

        var action = () => engine.RollbackAsync(
            workflow.RunId,
            cancellationToken: TestContext.Current.CancellationToken);

        await action.Should().ThrowAsync<WorkflowStateException>()
            .WithMessage("*different implementation key*");
    }

    [Fact]
    public async Task CancelledAttempt_DisposesItsScope()
    {
        var services = CreateServices<CancellationWorkflow, string, string>("cancellation");
        services.AddSingleton<CancellationProbe>();
        services.AddScoped<CancellationResource>();
        services.AddZhinuStep<CancellationStep, string, string>(CancellationKey);
        await using var provider = services.BuildServiceProvider();
        var engine = provider.GetRequiredService<WorkflowEngine>();
        var probe = provider.GetRequiredService<CancellationProbe>();
        var runId = await engine.StartAsync(
            "cancellation",
            "1",
            "value",
            cancellationToken: TestContext.Current.CancellationToken);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);

        var execution = engine.ExecuteAsync(runId, cancellation.Token);
        await probe.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();
        await execution;

        await probe.Disposed.Task.WaitAsync(TestContext.Current.CancellationToken);
        probe.DisposeCount.Should().Be(1);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    private ServiceCollection CreateServices<TWorkflow, TInput, TOutput>(
        string workflowName)
        where TWorkflow : class, IWorkflow<TInput, TOutput>
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddZhinuSqlite(options =>
            options.DatabasePath = Path.Combine(root, $"{workflowName}.db"));
        services.AddZhinu(options =>
            options.PollInterval = TimeSpan.FromMilliseconds(5));
        services.AddZhinuWorkflow<TWorkflow, TInput, TOutput>(workflowName, "1");
        return services;
    }

    private sealed class TwoKeyWorkflow : IWorkflow<string, string>
    {
        public Guid RunId { get; private set; }

        public async Task<string> RunAsync(
            WorkflowContext context,
            string input,
            CancellationToken cancellationToken)
        {
            RunId = context.WorkflowRunId;
            var plan = await context.StepAsync<string, string>(
                "plan",
                PlanningKey,
                input,
                cancellationToken: cancellationToken);
            var architecture = await context.StepAsync<string, string>(
                "architecture",
                ArchitectureKey,
                input,
                cancellationToken: cancellationToken);
            return $"{plan}|{architecture}";
        }
    }

    private sealed class TypedReferenceWorkflow : IWorkflow<string, string>
    {
        public Task<string> RunAsync(
            WorkflowContext context,
            string input,
            CancellationToken cancellationToken) =>
            context.StepAsync(
                "typed",
                PlanningReference,
                input,
                cancellationToken: cancellationToken);
    }

    private sealed class TypedFanOutWorkflow : IWorkflow<string, string>
    {
        public Guid RunId { get; private set; }

        public async Task<string> RunAsync(
            WorkflowContext context,
            string input,
            CancellationToken cancellationToken)
        {
            RunId = context.WorkflowRunId;
            var results = await context.FanOutAsync(
                "items",
                PlanningReference,
                input.Split(','),
                cancellationToken: cancellationToken);
            return string.Join('|', results);
        }
    }

    private sealed class EventWorkflow : IWorkflow<string, string>
    {
        public Task<string> RunAsync(
            WorkflowContext context,
            string input,
            CancellationToken cancellationToken) =>
            context.StepAsync(
                "event",
                EventReference,
                input,
                new StepOptions
                {
                    Retry = new RetryPolicy { MaxAttempts = 1 }
                },
                cancellationToken);
    }

    private sealed class EventEmittingStep(EventProbe probe) :
        WorkflowStep<string, string>
    {
        public override async Task<string> ExecuteAsync(
            WorkflowStepContext context,
            string input,
            CancellationToken cancellationToken)
        {
            await context.EmitAsync(
                "step-progress",
                new { input },
                cancellationToken);
            if (probe.Fail)
                throw new InvalidOperationException("Injected failure.");
            return input;
        }
    }

    private sealed class EventProbe
    {
        public bool Fail { get; init; }
    }

    private sealed class PlanningStep : CompensatingWorkflowStep<string, string>
    {
        public override Task<string> ExecuteAsync(
            WorkflowStepContext context,
            string input,
            CancellationToken cancellationToken) =>
            Task.FromResult($"plan:{input}");

        public override Task CompensateAsync(
            WorkflowStepContext context,
            string input,
            string output,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class ArchitectureStep : CompensatingWorkflowStep<string, string>
    {
        public override Task<string> ExecuteAsync(
            WorkflowStepContext context,
            string input,
            CancellationToken cancellationToken) =>
            Task.FromResult($"architecture:{input}");

        public override Task CompensateAsync(
            WorkflowStepContext context,
            string input,
            string output,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class CompensatingWorkflow : IWorkflow<string, string>
    {
        public Guid RunId { get; private set; }

        public bool UseChangedReplayInput { get; set; }

        public async Task<string> RunAsync(
            WorkflowContext context,
            string input,
            CancellationToken cancellationToken)
        {
            RunId = context.WorkflowRunId;
            return await context.StepAsync<string, string>(
                "reserve",
                ReservationKey,
                UseChangedReplayInput ? "changed" : input,
                new StepOptions
                {
                    Retry = new RetryPolicy { MaxAttempts = 2 }
                },
                cancellationToken: cancellationToken,
                compensation: StepCompensationMode.Enabled);
        }
    }

    private sealed class ReservationStep(
        AttemptResource resource,
        CompensationProbe probe) : CompensatingWorkflowStep<string, string>
    {
        private readonly Guid instanceId = Guid.NewGuid();

        public override Task<string> ExecuteAsync(
            WorkflowStepContext context,
            string input,
            CancellationToken cancellationToken)
        {
            probe.StepInstances.Add(instanceId);
            probe.ResourceInstances.Add(resource.Id);
            probe.ExecutionInputs.Add(input);
            return Task.FromResult($"reserved:{input}");
        }

        public override Task CompensateAsync(
            WorkflowStepContext context,
            string input,
            string output,
            CancellationToken cancellationToken)
        {
            probe.StepInstances.Add(instanceId);
            probe.ResourceInstances.Add(resource.Id);
            probe.CompensationInputs.Add(input);
            probe.CompensationOutputs.Add(output);
            if (Interlocked.Increment(ref probe.CompensationAttempts) == 1 &&
                probe.FailFirstCompensation)
            {
                throw new IOException("Injected compensation failure.");
            }
            return Task.CompletedTask;
        }
    }

    private sealed class MissingStepWorkflow : IWorkflow<string, string>
    {
        public Task<string> RunAsync(
            WorkflowContext context,
            string input,
            CancellationToken cancellationToken) =>
            context.StepAsync<string, string>(
                "missing",
                PlanningKey,
                input,
                cancellationToken: cancellationToken);
    }

    private sealed class ExecutionOnlyWorkflow : IWorkflow<string, string>
    {
        public Task<string> RunAsync(
            WorkflowContext context,
            string input,
            CancellationToken cancellationToken) =>
            context.StepAsync<string, string>(
                "execution-only",
                ExecutionOnlyKey,
                input,
                cancellationToken: cancellationToken,
                compensation: StepCompensationMode.Enabled);
    }

    private sealed class CustomResolverWorkflow : IWorkflow<string, string>
    {
        public Task<string> RunAsync(
            WorkflowContext context,
            string input,
            CancellationToken cancellationToken) =>
            context.StepAsync<string, string>(
                "custom-resolver",
                ExecutionOnlyKey,
                input,
                cancellationToken: cancellationToken);
    }

    private sealed class ExecutionOnlyStep(ExecutionOnlyProbe probe) :
        WorkflowStep<string, string>
    {
        public override Task<string> ExecuteAsync(
            WorkflowStepContext context,
            string input,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref probe.ExecutionCount);
            return Task.FromResult(input);
        }
    }

    private sealed class ExecutionOnlyProbe
    {
        public int ExecutionCount;
    }

    private sealed class FixedWorkflowStepResolver(
        StepImplementationKey implementationKey,
        object step) : IWorkflowStepResolver
    {
        public int DisposeCount;

        public ValueTask<IWorkflowStepLease<TStep>> ResolveAsync<TStep>(
            StepImplementationKey requestedKey,
            CancellationToken cancellationToken)
            where TStep : class
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (requestedKey != implementationKey || step is not TStep typedStep)
            {
                return ValueTask.FromException<IWorkflowStepLease<TStep>>(
                    new WorkflowConfigurationException("No matching test step."));
            }
            return ValueTask.FromResult<IWorkflowStepLease<TStep>>(
                new FixedWorkflowStepLease<TStep>(typedStep, this));
        }

        private sealed class FixedWorkflowStepLease<TStep>(
            TStep step,
            FixedWorkflowStepResolver owner) : IWorkflowStepLease<TStep>
            where TStep : class
        {
            public TStep Step { get; } = step;

            public ValueTask DisposeAsync()
            {
                Interlocked.Increment(ref owner.DisposeCount);
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class MutableKeyWorkflow : IWorkflow<string, string>
    {
        public Guid RunId { get; private set; }

        public StepImplementationKey ImplementationKey { get; set; } = PlanningKey;

        public async Task<string> RunAsync(
            WorkflowContext context,
            string input,
            CancellationToken cancellationToken)
        {
            RunId = context.WorkflowRunId;
            return await context.StepAsync<string, string>(
                "mutable",
                ImplementationKey,
                input,
                cancellationToken: cancellationToken,
                compensation: StepCompensationMode.Enabled);
        }
    }

    private sealed class AttemptResource(CompensationProbe probe) : IAsyncDisposable
    {
        public Guid Id { get; } = Guid.NewGuid();

        public ValueTask DisposeAsync()
        {
            probe.DisposedResources.Add(Id);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CompensationProbe
    {
        public bool FailFirstCompensation { get; set; }
        public int CompensationAttempts;
        public ConcurrentBag<Guid> StepInstances { get; } = [];
        public ConcurrentBag<Guid> ResourceInstances { get; } = [];
        public ConcurrentBag<Guid> DisposedResources { get; } = [];
        public ConcurrentBag<string> ExecutionInputs { get; } = [];
        public ConcurrentBag<string> CompensationInputs { get; } = [];
        public ConcurrentBag<string> CompensationOutputs { get; } = [];
    }

    private sealed class DisposalWorkflow : IWorkflow<string, string>
    {
        public Task<string> RunAsync(
            WorkflowContext context,
            string input,
            CancellationToken cancellationToken) =>
            context.StepAsync<string, string>(
                "dispose",
                DisposalKey,
                input,
                new StepOptions
                {
                    Retry = new RetryPolicy { MaxAttempts = 2 }
                },
                cancellationToken);
    }

    private sealed class DisposalStep(
        FailFirstDisposalResource resource,
        DisposalProbe probe) : WorkflowStep<string, string>
    {
        private readonly Guid instanceId = Guid.NewGuid();

        public override Task<string> ExecuteAsync(
            WorkflowStepContext context,
            string input,
            CancellationToken cancellationToken)
        {
            probe.StepInstances.Add(instanceId);
            probe.ResourceInstances.Add(resource.Id);
            Interlocked.Increment(ref probe.ExecuteCount);
            return Task.FromResult(input.ToUpperInvariant());
        }
    }

    private sealed class FailFirstDisposalResource(DisposalProbe probe) : IAsyncDisposable
    {
        public Guid Id { get; } = Guid.NewGuid();

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Increment(ref probe.DisposeCount) == 1)
                return ValueTask.FromException(new IOException("Injected disposal failure."));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class DisposalProbe
    {
        public int ExecuteCount;
        public int DisposeCount;
        public ConcurrentBag<Guid> StepInstances { get; } = [];
        public ConcurrentBag<Guid> ResourceInstances { get; } = [];
    }

    private sealed class CancellationWorkflow : IWorkflow<string, string>
    {
        public Task<string> RunAsync(
            WorkflowContext context,
            string input,
            CancellationToken cancellationToken) =>
            context.StepAsync<string, string>(
                "cancel",
                CancellationKey,
                input,
                cancellationToken: cancellationToken);
    }

    private sealed class CancellationStep(
        CancellationResource resource,
        CancellationProbe probe) : WorkflowStep<string, string>
    {
        public override async Task<string> ExecuteAsync(
            WorkflowStepContext context,
            string input,
            CancellationToken cancellationToken)
        {
            _ = resource;
            probe.Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return input;
        }
    }

    private sealed class CancellationResource(CancellationProbe probe) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref probe.DisposeCount);
            probe.Disposed.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CancellationProbe
    {
        public int DisposeCount;
        public TaskCompletionSource Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Disposed { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
