using System.Text.Json;
using FluentAssertions;
using Penghou.Workflow.Abstractions;
using Penghou.Zhinu.Declarative;

namespace Penghou.Zhinu.Sqlite.Tests;

public sealed class DeclarativeAuthorizationTests : WorkflowEngineTestBase
{
    [Fact]
    public void Fingerprint_WithoutDeclaration_PreservesPreviousCanonicalIdentity()
    {
        var catalogue = new ActivityCatalogue();
        catalogue.Register(new ActivityReference("echo", "1"), new EchoActivity());
        var definition = new DeclarativeWorkflowDefinition
        {
            Name = "baseline",
            Version = "1",
            Steps = [new DeclarativeWorkflowStep
            {
                Id = "run",
                Activity = new ActivityReference("echo", "1")
            }]
        };

        WorkflowCompiler.Compile(definition, catalogue).Compiled!.Fingerprint
            .Should().Be("7067ce71fd41e27d478caffb489a98e64788c02f20ba9d1525e89c6c510604f6");
    }

    [Fact]
    public void Fingerprint_ChangesWhenRequirementsChange()
    {
        static string CompileFingerprint(string capability)
        {
            var catalogue = new ActivityCatalogue();
            catalogue.Register(new ActivityReference("echo", "1"), new EchoActivity(),
                Declaration(capability));
            var definition = new DeclarativeWorkflowDefinition
            {
                Name = "auth-plan",
                Version = "7",
                Steps = [new DeclarativeWorkflowStep
                {
                    Id = "run",
                    Activity = new ActivityReference("echo", "1")
                }]
            };
            return WorkflowCompiler.Compile(definition, catalogue).Compiled!.Fingerprint;
        }

        CompileFingerprint("read").Should().NotBe(CompileFingerprint("write"));
    }

    [Fact]
    public void Compile_SnapshotsActivityDeclaration()
    {
        var requirements = new List<ExecutionRequirement>
        {
            new("profile", 1, "read", "document:1")
        };
        var declaration = new WorkflowAuthorizationDeclaration(requirements);
        var catalogue = new ActivityCatalogue();
        catalogue.Register(new ActivityReference("echo", "1"), new EchoActivity(), declaration);
        requirements[0] = new ExecutionRequirement("profile", 1, "write", "document:1");
        var compiled = WorkflowCompiler.Compile(new DeclarativeWorkflowDefinition
        {
            Name = "auth-plan",
            Version = "7",
            Steps = [new DeclarativeWorkflowStep
            {
                Id = "run",
                Activity = new ActivityReference("echo", "1")
            }]
        }, catalogue).Compiled!;

        compiled.Steps[0].Descriptor.Authorization!.Requirements.Single().Capability
            .Should().Be("read");
    }

    [Fact]
    public async Task DeniedActivity_DoesNotResolveOrInvokeExecutor()
    {
        var catalogue = new TrackingCatalogue();
        catalogue.Register(new ActivityReference("echo", "1"), Declaration("read"));
        var compiled = WorkflowCompiler.Compile(new DeclarativeWorkflowDefinition
        {
            Name = "auth-plan",
            Version = "7",
            Steps = [new DeclarativeWorkflowStep
            {
                Id = "run",
                Activity = new ActivityReference("echo", "1")
            }]
        }, catalogue).Compiled!;
        var options = new ZhinuOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(10),
            ExecutionAuthorization = new WorkflowExecutionAuthorizationOptions(
                "policy", "host-v1", new DenyingAuthorizer())
        };
        var registry = new WorkflowRegistry().RegisterDeclarative(compiled, catalogue);
        var engine = new WorkflowEngine(CreateStore(), registry, options);
        var runId = await engine.StartAsync<JsonElement>(
            "auth-plan", "7", JsonSerializer.SerializeToElement("input"),
            cancellationToken: TestContext.Current.CancellationToken);

        var run = () => engine.WaitForCompletionAsync<JsonElement>(
            runId, cancellationToken: TestContext.Current.CancellationToken);
        await run.Should().ThrowAsync<WorkflowExecutionFailedException>();
        catalogue.ResolveCount.Should().Be(0);
        catalogue.ExecuteCount.Should().Be(0);
    }

    [Fact]
    public async Task DeniedLoopPredicate_DoesNotInvokePredicateOrBody()
    {
        var workflow = new LoopWorkflow();
        var options = new ZhinuOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(10),
            ExecutionAuthorization = new WorkflowExecutionAuthorizationOptions(
                "policy", "host-v1", new DenyingAuthorizer())
        };
        var engine = new WorkflowEngine(
            CreateStore(),
            new WorkflowRegistry().Register("loop", "1", workflow),
            options);
        var runId = await engine.StartAsync("loop", "1", "input",
            cancellationToken: TestContext.Current.CancellationToken);

        var run = () => engine.WaitForCompletionAsync<string>(
            runId, cancellationToken: TestContext.Current.CancellationToken);
        await run.Should().ThrowAsync<WorkflowExecutionFailedException>();
        workflow.PredicateCount.Should().Be(0);
        workflow.BodyCount.Should().Be(0);
    }

    private static WorkflowAuthorizationDeclaration Declaration(string capability) =>
        new([new ExecutionRequirement("profile", 1, capability, "document:1")]);

    private sealed class EchoActivity : IActivity<string, string>
    {
        public Task<string> ExecuteAsync(string input, CancellationToken cancellationToken) => Task.FromResult(input);
    }

    private sealed class LoopWorkflow : IWorkflow<string, string>
    {
        public int PredicateCount { get; private set; }
        public int BodyCount { get; private set; }

        public async Task<string> RunAsync(WorkflowContext context, string input,
            CancellationToken cancellationToken)
        {
            var state = await context.LoopAsync(
                "loop",
                0,
                _ =>
                {
                    PredicateCount++;
                    return true;
                },
                (iteration, _) =>
                {
                    BodyCount++;
                    return Task.FromResult(iteration.Break(iteration.State));
                },
                new LoopOptions(3)
                {
                    ContinueWhileAuthorization = Declaration("continue")
                },
                cancellationToken);
            return state.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    private sealed class DenyingAuthorizer : IExecutionAuthorizer
    {
        public ValueTask<ExecutionAuthorizationResult> AuthorizeAsync(
            ExecutionAuthorizationContext context,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ExecutionAuthorizationResult(
                ExecutionAuthorizationDecision.Denied,
                context.AuthorizationRequestId,
                "policy",
                "denied-1",
                DateTimeOffset.UtcNow));
    }

    private sealed class TrackingCatalogue : IActivityCatalogue, IActivityExecutorResolver
    {
        private ActivityDescriptor? descriptor;

        public int ResolveCount { get; private set; }
        public int ExecuteCount { get; private set; }

        public void Register(ActivityReference reference, WorkflowAuthorizationDeclaration authorization) =>
            descriptor = new ActivityDescriptor
            {
                Reference = reference,
                Input = new ActivityContract { TypeId = "System.String, System.Private.CoreLib" },
                Output = new ActivityContract { TypeId = "System.String, System.Private.CoreLib" },
                Authorization = authorization
            };

        public void Register<TInput, TOutput>(ActivityReference reference, IActivity<TInput, TOutput> implementation) =>
            throw new NotSupportedException();

        public void Register<TInput, TOutput>(ActivityReference reference, IActivity<TInput, TOutput> implementation,
            WorkflowAuthorizationDeclaration authorization) =>
            Register(reference, authorization);

        public ActivityDescriptor GetDescriptor(ActivityReference reference) =>
            descriptor is { } value && value.Reference == reference
                ? value
                : throw new KeyNotFoundException();

        public IReadOnlyList<ActivityDescriptor> ListDescriptors() =>
            descriptor is { } value ? [value] : [];

        public bool TryGetDescriptor(ActivityReference reference, out ActivityDescriptor result)
        {
            result = descriptor!;
            return descriptor is { } value && value.Reference == reference;
        }

        public IActivityExecutor Resolve(ActivityReference reference)
        {
            ResolveCount++;
            return new TrackingExecutor(this);
        }

        private sealed class TrackingExecutor(TrackingCatalogue owner) : IActivityExecutor
        {
            public Type InputType => typeof(string);
            public Type OutputType => typeof(string);

            public Task<object?> ExecuteAsync(object? input, CancellationToken cancellationToken)
            {
                owner.ExecuteCount++;
                return Task.FromResult(input);
            }
        }
    }
}
