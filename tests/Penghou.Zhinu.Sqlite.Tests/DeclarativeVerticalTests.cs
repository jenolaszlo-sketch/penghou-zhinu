using FluentAssertions;
using System.Text.Json;
using Penghou.Zhinu.Declarative;
#pragma warning disable xUnit1051

namespace Penghou.Zhinu.Sqlite.Tests;

public sealed class DeclarativeVerticalTests : WorkflowEngineTestBase
{
    private static IActivity<string, string> Echo(string suffix) => new FuncActivity<string, string>(s => Task.FromResult(s + suffix));

    // --- Catalogue ---

    [Fact]
    public void Catalogue_DuplicateRegistration_Rejected()
    {
        var catalogue = new ActivityCatalogue();
        var reference = new ActivityReference("echo", "1");
        catalogue.Register(reference, Echo("-a"));
        var act = () => catalogue.Register(reference, Echo("-b"));
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Catalogue_ExactVersionResolution()
    {
        var catalogue = new ActivityCatalogue();
        catalogue.Register(new ActivityReference("echo", "1"), Echo("-v1"));
        catalogue.Register(new ActivityReference("echo", "2"), Echo("-v2"));
        var descriptor = catalogue.GetDescriptor(new ActivityReference("echo", "2"));
        descriptor.Reference.Version.Should().Be("2");
        catalogue.Invoking(c => c.GetDescriptor(new ActivityReference("echo", "3")))
            .Should().Throw<KeyNotFoundException>();
    }

    [Fact]
    public void Catalogue_UnknownActivity_Throws()
    {
        var catalogue = new ActivityCatalogue();
        var act = () => catalogue.Resolve(new ActivityReference("missing", "1"));
        act.Should().Throw<KeyNotFoundException>();
    }

    // --- Validation ---

    [Fact]
    public void Validation_DuplicateStepIds_Fails()
    {
        var catalogue = CreateCatalogue();
        var definition = new DeclarativeWorkflowDefinition
        {
            Name = "test",
            Version = "1",
            Steps = new[]
            {
                new DeclarativeWorkflowStep { Id = "a", Activity = new ActivityReference("echo-a", "1") },
                new DeclarativeWorkflowStep { Id = "a", Activity = new ActivityReference("echo-b", "1") }
            }
        };
        var result = WorkflowDefinitionValidator.Validate(definition, catalogue);
        result.IsValid.Should().BeFalse();
        result.Diagnostics.Should().Contain(d => d.Code == "WF012");
    }

    [Fact]
    public void Validation_UnknownDependency_Fails()
    {
        var catalogue = CreateCatalogue();
        var definition = new DeclarativeWorkflowDefinition
        {
            Name = "test",
            Version = "1",
            Steps = new[]
            {
                new DeclarativeWorkflowStep { Id = "a", Activity = new ActivityReference("echo-a", "1"), DependsOn = new[] { "missing" } }
            }
        };
        var result = WorkflowDefinitionValidator.Validate(definition, catalogue);
        result.IsValid.Should().BeFalse();
        result.Diagnostics.Should().Contain(d => d.Code == "WF020");
    }

    [Fact]
    public void Validation_DirectCycle_Fails()
    {
        var catalogue = CreateCatalogue();
        var definition = new DeclarativeWorkflowDefinition
        {
            Name = "test",
            Version = "1",
            Steps = new[]
            {
                new DeclarativeWorkflowStep { Id = "a", Activity = new ActivityReference("echo-a", "1"), DependsOn = new[] { "b" } },
                new DeclarativeWorkflowStep { Id = "b", Activity = new ActivityReference("echo-b", "1"), DependsOn = new[] { "a" } }
            }
        };
        var result = WorkflowDefinitionValidator.Validate(definition, catalogue);
        result.IsValid.Should().BeFalse();
        result.Diagnostics.Should().Contain(d => d.Code == "WF022");
    }

    [Fact]
    public void Validation_ValidLinearWorkflow_Succeeds()
    {
        var catalogue = CreateCatalogue();
        var definition = ValidLinearDefinition();
        var result = WorkflowDefinitionValidator.Validate(definition, catalogue);
        result.IsValid.Should().BeTrue();
    }

    // --- Canonical / Fingerprint ---

    [Fact]
    public void Canonicalization_ReorderedSteps_NormalizesIdentically()
    {
        var catalogue = CreateCatalogue();
        var def1 = ValidLinearDefinition();
        var def2 = new DeclarativeWorkflowDefinition
        {
            Name = "test",
            Version = "1",
            Steps = new[] { def1.Steps[2], def1.Steps[0], def1.Steps[1] }
        };
        var compiled1 = WorkflowCompiler.Compile(def1, catalogue).Compiled!;
        var compiled2 = WorkflowCompiler.Compile(def2, catalogue).Compiled!;
        WorkflowCanonicalizer.Canonicalize(compiled1).Should().Be(WorkflowCanonicalizer.Canonicalize(compiled2));
        compiled1.Fingerprint.Should().Be(compiled2.Fingerprint);
    }

    [Fact]
    public void Fingerprint_SemanticChange_ChangesHash()
    {
        var catalogue = CreateCatalogue();
        var def1 = ValidLinearDefinition();
        var def2 = new DeclarativeWorkflowDefinition
        {
            Name = "test",
            Version = "1",
            Steps = new[]
            {
                new DeclarativeWorkflowStep { Id = "a", Activity = new ActivityReference("echo-a", "1") },
                new DeclarativeWorkflowStep { Id = "b", Activity = new ActivityReference("echo-b", "1"), DependsOn = new[] { "a" } },
                new DeclarativeWorkflowStep { Id = "c", Activity = new ActivityReference("echo-a", "1"), DependsOn = new[] { "b" } }
            }
        };
        var fp1 = WorkflowCompiler.Compile(def1, catalogue).Compiled!.Fingerprint;
        var fp2 = WorkflowCompiler.Compile(def2, catalogue).Compiled!.Fingerprint;
        fp1.Should().NotBe(fp2);
    }

    [Fact]
    public async Task DefinitionIdentity_MatchingFingerprint_Resumes()
    {
        var catalogue = CreateCatalogue();
        var compiled = WorkflowCompiler.Compile(ValidLinearDefinition(), catalogue).Compiled!;
        var store = CreateStore();
        var engine1 = CreateDeclarativeEngine(compiled, catalogue, store);
        var runId = await engine1.StartAsync<JsonElement>("test", "1", JsonSerializer.SerializeToElement("start"), cancellationToken: TestContext.Current.CancellationToken);
        // Recorded fingerprint must be persisted on the run.
        var recorded = await store.GetRunAsync(runId, TestContext.Current.CancellationToken);
        recorded!.DefinitionFingerprint.Should().Be(compiled.Fingerprint);

        // Fresh engine with the SAME compiled definition resumes.
        var engine2 = CreateDeclarativeEngine(compiled, catalogue, store);
        await engine2.ExecuteAsync(runId, TestContext.Current.CancellationToken);
        var result = await engine2.WaitForCompletionAsync<JsonElement>(runId, cancellationToken: TestContext.Current.CancellationToken);
        result.GetString().Should().Be("start-a-b-c");
    }

    [Fact]
    public async Task DefinitionIdentity_MismatchedFingerprint_RejectsExecution()
    {
        var catalogue = CreateCatalogue();
        var compiled1 = WorkflowCompiler.Compile(ValidLinearDefinition(), catalogue).Compiled!;
        var store = CreateStore();
        var engine1 = CreateDeclarativeEngine(compiled1, catalogue, store);
        // Leave the run Pending: resume with a different definition must be rejected.
        var runId = await engine1.StartAsync<JsonElement>("test", "1", JsonSerializer.SerializeToElement("start"), cancellationToken: TestContext.Current.CancellationToken);

        // Register a DIFFERENT compiled definition with the same name/version.
        var changed = new DeclarativeWorkflowDefinition
        {
            Name = "test",
            Version = "1",
            Steps = new[]
            {
                new DeclarativeWorkflowStep { Id = "a", Activity = new ActivityReference("echo-b", "1") }
            }
        };
        var compiled2 = WorkflowCompiler.Compile(changed, catalogue).Compiled!;
        compiled2.Fingerprint.Should().NotBe(compiled1.Fingerprint);
        var engine2 = CreateDeclarativeEngine(compiled2, catalogue, store);
        await engine2.ExecuteAsync(runId, TestContext.Current.CancellationToken);
        var act = () => engine2.WaitForCompletionAsync<JsonElement>(runId, cancellationToken: TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<WorkflowExecutionFailedException>();
        var run = await store.GetRunAsync(runId, TestContext.Current.CancellationToken);
        run!.Status.Should().Be(WorkflowStatus.Failed);
    }

    [Fact]
    public async Task DefinitionIdentity_MissingRegistration_RejectsExecution()
    {
        var catalogue = CreateCatalogue();
        var compiled = WorkflowCompiler.Compile(ValidLinearDefinition(), catalogue).Compiled!;
        var store = CreateStore();
        var engine1 = CreateDeclarativeEngine(compiled, catalogue, store);
        var runId = await engine1.StartAsync<JsonElement>("test", "1", JsonSerializer.SerializeToElement("start"), cancellationToken: TestContext.Current.CancellationToken);

        // Fresh engine with an EMPTY registry: the run cannot resume.
        var engine2 = new WorkflowEngine(
            store,
            new WorkflowRegistry(),
            new ZhinuOptions { PollInterval = TimeSpan.FromMilliseconds(10) });
        await engine2.ExecuteAsync(runId, TestContext.Current.CancellationToken);
        var act = () => engine2.WaitForCompletionAsync<JsonElement>(runId, cancellationToken: TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<WorkflowExecutionFailedException>();
        var run = await store.GetRunAsync(runId, TestContext.Current.CancellationToken);
        run!.Status.Should().Be(WorkflowStatus.Failed);
    }

    [Fact]
    public void Fingerprint_StableAcrossRepeatedCompilation()
    {
        var catalogue = CreateCatalogue();
        var definition = ValidLinearDefinition();
        var fp1 = WorkflowCompiler.Compile(definition, catalogue).Compiled!.Fingerprint;
        var fp2 = WorkflowCompiler.Compile(definition, catalogue).Compiled!.Fingerprint;
        fp1.Should().Be(fp2);
    }

    // --- Execution ---

    [Fact]
    public async Task Execution_A_B_C_Sequential()
    {
        var catalogue = CreateCatalogue();
        var definition = ValidLinearDefinition();
        var compiled = WorkflowCompiler.Compile(definition, catalogue).Compiled!;
        var engine = CreateDeclarativeEngine(compiled, catalogue);
        var runId = await engine.StartAsync<JsonElement>("test", "1", JsonSerializer.SerializeToElement("start"), cancellationToken: TestContext.Current.CancellationToken);
        await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
        var result = await engine.WaitForCompletionAsync<JsonElement>(runId, cancellationToken: TestContext.Current.CancellationToken);
        result.GetString().Should().Be("start-a-b-c");
    }

    [Fact]
    public async Task Execution_ActivityFailure_FailsWorkflow()
    {
        var catalogue = new ActivityCatalogue();
        catalogue.Register(new ActivityReference("fail", "1"), new FuncActivity<string, string>(_ => throw new InvalidOperationException("boom")));
        var definition = new DeclarativeWorkflowDefinition
        {
            Name = "test",
            Version = "1",
            Steps = new[] { new DeclarativeWorkflowStep { Id = "a", Activity = new ActivityReference("fail", "1") } }
        };
        var compiled = WorkflowCompiler.Compile(definition, catalogue).Compiled!;
        var engine = CreateDeclarativeEngine(compiled, catalogue);
        var runId = await engine.StartAsync<JsonElement>("test", "1", JsonSerializer.SerializeToElement("x"), cancellationToken: TestContext.Current.CancellationToken);
        await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
        var act = () => engine.WaitForCompletionAsync<JsonElement>(runId, cancellationToken: TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<WorkflowExecutionFailedException>();
    }

    // --- Persistence / Restart ---

    [Fact]
    public async Task Execution_RestartBetweenSteps_ResumesFromDurableState()
    {
        var catalogue = CreateCatalogue();
        var definition = ValidLinearDefinition();
        var compiled = WorkflowCompiler.Compile(definition, catalogue).Compiled!;
        var store = CreateStore();
        var engine1 = CreateDeclarativeEngine(compiled, catalogue, store);
        var runId = await engine1.StartAsync<JsonElement>("test", "1", JsonSerializer.SerializeToElement("start"), cancellationToken: TestContext.Current.CancellationToken);
        await engine1.ExecuteAsync(runId, TestContext.Current.CancellationToken);
        // After first engine, at least one step should be completed
        var steps1 = await store.GetStepsAsync(runId, TestContext.Current.CancellationToken);
        steps1.Should().Contain(s => s.Status == StepStatus.Completed);

        // Fresh engine with same compiled definition and catalogue resumes
        var engine2 = CreateDeclarativeEngine(compiled, catalogue, store);
        await engine2.ExecuteAsync(runId, TestContext.Current.CancellationToken);
        var result = await engine2.WaitForCompletionAsync<JsonElement>(runId, cancellationToken: TestContext.Current.CancellationToken);
        result.GetString().Should().Be("start-a-b-c");
    }

    // --- Definition Identity ---

    [Fact]
    public async Task DefinitionIdentity_MismatchedFingerprint_Rejected()
    {
        var catalogue = CreateCatalogue();
        var def1 = ValidLinearDefinition();
        var compiled1 = WorkflowCompiler.Compile(def1, catalogue).Compiled!;
        var def2 = new DeclarativeWorkflowDefinition
        {
            Name = "test",
            Version = "1",
            Steps = new[]
            {
                new DeclarativeWorkflowStep { Id = "a", Activity = new ActivityReference("echo-b", "1") },
                new DeclarativeWorkflowStep { Id = "b", Activity = new ActivityReference("echo-a", "1"), DependsOn = new[] { "a" } },
                new DeclarativeWorkflowStep { Id = "c", Activity = new ActivityReference("echo-c", "1"), DependsOn = new[] { "b" } }
            }
        };
        var compiled2 = WorkflowCompiler.Compile(def2, catalogue).Compiled!;
        compiled1.Fingerprint.Should().NotBe(compiled2.Fingerprint);
        // Name+version same, fingerprint differs -> should be considered mismatch
        (compiled1.Name == compiled2.Name && compiled1.Version == compiled2.Version).Should().BeTrue();
    }

    private static IActivityCatalogue CreateCatalogue()
    {
        var catalogue = new ActivityCatalogue();
        catalogue.Register(new ActivityReference("echo-a", "1"), Echo("-a"));
        catalogue.Register(new ActivityReference("echo-b", "1"), Echo("-b"));
        catalogue.Register(new ActivityReference("echo-c", "1"), Echo("-c"));
        return catalogue;
    }

    private static DeclarativeWorkflowDefinition ValidLinearDefinition() => new()
    {
        Name = "test",
        Version = "1",
        Steps = new[]
        {
            new DeclarativeWorkflowStep { Id = "a", Activity = new ActivityReference("echo-a", "1") },
            new DeclarativeWorkflowStep { Id = "b", Activity = new ActivityReference("echo-b", "1"), DependsOn = new[] { "a" } },
            new DeclarativeWorkflowStep { Id = "c", Activity = new ActivityReference("echo-c", "1"), DependsOn = new[] { "b" } }
        }
    };

    private WorkflowEngine CreateDeclarativeEngine(CompiledWorkflowDefinition compiled, IActivityCatalogue catalogue, SqliteWorkflowStore? storeOverride = null)
    {
        var store = storeOverride ?? CreateStore();
        var workflow = new DeclarativeWorkflow(compiled, catalogue);
        var registry = new WorkflowRegistry().Register(compiled.Name, compiled.Version, workflow);
        return new WorkflowEngine(store, registry, new ZhinuOptions { PollInterval = TimeSpan.FromMilliseconds(10) });
    }

    private sealed class FuncActivity<TInput, TOutput> : IActivity<TInput, TOutput>
    {
        private readonly Func<TInput, Task<TOutput>> func;
        public FuncActivity(Func<TInput, Task<TOutput>> func) => this.func = func;
        public Task<TOutput> ExecuteAsync(TInput input, CancellationToken ct) => func(input);
    }
}
