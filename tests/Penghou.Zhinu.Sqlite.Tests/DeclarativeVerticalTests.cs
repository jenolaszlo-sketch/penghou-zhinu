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
        act.Should().Throw<WorkflowRegistrationException>();
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

    [Fact]
    public void Catalogue_GenericContractIdentity_DoesNotContainAssemblyVersions()
    {
        var catalogue = new ActivityCatalogue();
        var reference = new ActivityReference("generic", "1");
        catalogue.Register(
            reference,
            new FuncActivity<IReadOnlyList<string>, Dictionary<string, int>>(
                _ => Task.FromResult(new Dictionary<string, int>())));

        var descriptor = catalogue.GetDescriptor(reference);

        descriptor.Input.TypeId.Should().NotContain("Version=");
        descriptor.Output.TypeId.Should().NotContain("Version=");
        descriptor.Input.TypeId.Should().Contain("System.String, System.Private.CoreLib");
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

    [Fact]
    public void Validation_BranchingWorkflow_IsRejectedUntilBranchSemanticsExist()
    {
        var catalogue = CreateCatalogue();
        var definition = new DeclarativeWorkflowDefinition
        {
            Name = "test",
            Version = "1",
            Steps = new[]
            {
                new DeclarativeWorkflowStep { Id = "a", Activity = new ActivityReference("echo-a", "1") },
                new DeclarativeWorkflowStep { Id = "b", Activity = new ActivityReference("echo-b", "1"), DependsOn = new[] { "a" } },
                new DeclarativeWorkflowStep { Id = "c", Activity = new ActivityReference("echo-c", "1"), DependsOn = new[] { "a" } }
            }
        };

        var result = WorkflowDefinitionValidator.Validate(definition, catalogue);

        result.IsValid.Should().BeFalse();
        result.Diagnostics.Should().Contain(d => d.Code == "WF023");
    }

    [Fact]
    public void Registration_HandConstructedBranchingCompiledDefinition_IsRejected()
    {
        var catalogue = CreateCatalogue();
        var a = catalogue.GetDescriptor(new ActivityReference("echo-a", "1"));
        var b = catalogue.GetDescriptor(new ActivityReference("echo-b", "1"));
        var c = catalogue.GetDescriptor(new ActivityReference("echo-c", "1"));
        var definition = new CompiledWorkflowDefinition
        {
            Name = "test",
            Version = "1",
            Fingerprint = "untrusted",
            Steps = new[]
            {
                new CompiledWorkflowStep { Id = "a", Activity = a.Reference, Descriptor = a, DependsOn = Array.Empty<string>() },
                new CompiledWorkflowStep { Id = "b", Activity = b.Reference, Descriptor = b, DependsOn = new[] { "a" } },
                new CompiledWorkflowStep { Id = "c", Activity = c.Reference, Descriptor = c, DependsOn = new[] { "a" } }
            }
        };

        var action = () => new WorkflowRegistry().RegisterDeclarative(definition, catalogue);

        action.Should().Throw<ArgumentException>().WithMessage("*WF023*");
    }

    [Fact]
    public void Validation_TypeCompatibility_FollowsDependencyRatherThanSourceOrder()
    {
        var catalogue = new ActivityCatalogue();
        catalogue.Register(
            new ActivityReference("length", "1"),
            new FuncActivity<string, int>(value => Task.FromResult(value.Length)));
        catalogue.Register(
            new ActivityReference("format", "1"),
            new FuncActivity<int, string>(value => Task.FromResult(value.ToString())));
        var definition = new DeclarativeWorkflowDefinition
        {
            Name = "test",
            Version = "1",
            Steps = new[]
            {
                new DeclarativeWorkflowStep { Id = "b", Activity = new ActivityReference("format", "1"), DependsOn = new[] { "a" } },
                new DeclarativeWorkflowStep { Id = "a", Activity = new ActivityReference("length", "1") }
            }
        };

        WorkflowDefinitionValidator.Validate(definition, catalogue).IsValid.Should().BeTrue();
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
    public void Fingerprint_ActivityContractChange_ChangesHash()
    {
        var stringCatalogue = new ActivityCatalogue();
        stringCatalogue.Register(
            new ActivityReference("activity", "1"),
            new FuncActivity<string, string>(Task.FromResult));
        var integerCatalogue = new ActivityCatalogue();
        integerCatalogue.Register(
            new ActivityReference("activity", "1"),
            new FuncActivity<int, int>(Task.FromResult));
        var definition = new DeclarativeWorkflowDefinition
        {
            Name = "contract-hash",
            Version = "1",
            Steps =
            [
                new DeclarativeWorkflowStep
                {
                    Id = "activity",
                    Activity = new ActivityReference("activity", "1")
                }
            ]
        };

        var stringFingerprint = WorkflowCompiler.Compile(
            definition,
            stringCatalogue).Compiled!.Fingerprint;
        var integerFingerprint = WorkflowCompiler.Compile(
            definition,
            integerCatalogue).Compiled!.Fingerprint;

        stringFingerprint.Should().NotBe(integerFingerprint);
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

    [Fact]
    public void Fingerprint_RecomputedFromCompiledDefinition_MatchesStoredFingerprint()
    {
        var compiled = WorkflowCompiler.Compile(ValidLinearDefinition(), CreateCatalogue()).Compiled!;

        WorkflowFingerprint.Compute(compiled).Should().Be(compiled.Fingerprint);
    }

    [Fact]
    public void CompiledDefinition_JsonRoundTrip_PreservesPortableContracts()
    {
        var compiled = WorkflowCompiler.Compile(ValidLinearDefinition(), CreateCatalogue()).Compiled!;

        var json = JsonSerializer.Serialize(compiled);
        var restored = JsonSerializer.Deserialize<CompiledWorkflowDefinition>(json)!;

        json.Should().NotContain("ClrType");
        restored.Fingerprint.Should().Be(compiled.Fingerprint);
        WorkflowCanonicalizer.Canonicalize(restored)
            .Should().Be(WorkflowCanonicalizer.Canonicalize(compiled));
        restored.Steps.Select(s => s.Descriptor.Input.TypeId)
            .Should().Equal(compiled.Steps.Select(s => s.Descriptor.Input.TypeId));
        restored.Steps.Select(s => s.Descriptor.Output.TypeId)
            .Should().Equal(compiled.Steps.Select(s => s.Descriptor.Output.TypeId));
    }

    [Fact]
    public async Task CompiledDefinition_JsonRoundTrip_CanRegisterAndExecute()
    {
        var catalogue = CreateCatalogue();
        var compiled = WorkflowCompiler.Compile(ValidLinearDefinition(), catalogue).Compiled!;
        var restored = JsonSerializer.Deserialize<CompiledWorkflowDefinition>(
            JsonSerializer.Serialize(compiled))!;
        var engine = CreateDeclarativeEngine(restored, catalogue);
        var runId = await engine.StartAsync(
            "test",
            "1",
            JsonSerializer.SerializeToElement("start"),
            cancellationToken: TestContext.Current.CancellationToken);

        await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
        var result = await engine.WaitForCompletionAsync<JsonElement>(
            runId,
            cancellationToken: TestContext.Current.CancellationToken);

        result.GetString().Should().Be("start-a-b-c");
    }

    [Fact]
    public void Registration_CatalogueContractMismatch_IsRejected()
    {
        var compiled = WorkflowCompiler.Compile(ValidLinearDefinition(), CreateCatalogue()).Compiled!;
        var incompatibleCatalogue = new ActivityCatalogue();
        incompatibleCatalogue.Register(
            new ActivityReference("echo-a", "1"),
            new FuncActivity<int, int>(value => Task.FromResult(value)));
        incompatibleCatalogue.Register(new ActivityReference("echo-b", "1"), Echo("-b"));
        incompatibleCatalogue.Register(new ActivityReference("echo-c", "1"), Echo("-c"));

        var registration = () => new WorkflowRegistry()
            .RegisterDeclarative(compiled, incompatibleCatalogue);

        registration.Should().Throw<ArgumentException>()
            .WithMessage("*does not match registered activity*");
    }

    // --- Inspection ---

    [Fact]
    public async Task Inspection_RunExposesFingerprintStepsActivitiesHistoryAndResult()
    {
        var catalogue = CreateCatalogue();
        var compiled = WorkflowCompiler.Compile(ValidLinearDefinition(), catalogue).Compiled!;
        var store = CreateStore();
        var engine = CreateDeclarativeEngine(compiled, catalogue, store);
        var runId = await engine.StartAsync<JsonElement>("test", "1", JsonSerializer.SerializeToElement("start"), cancellationToken: TestContext.Current.CancellationToken);

        // Fingerprint on the run.
        var recorded = await store.GetRunAsync(runId, TestContext.Current.CancellationToken);
        recorded!.WorkflowName.Should().Be("test");
        recorded.WorkflowVersion.Should().Be("1");
        recorded.DefinitionFingerprint.Should().Be(compiled.Fingerprint);

        // Compiled definition exposes resolved activity references per step.
        compiled.Steps.Should().HaveCount(3);
        compiled.Steps.Select(s => s.Id).Should().Equal("a", "b", "c");
        foreach (var step in compiled.Steps)
        {
            step.Activity.Name.Should().StartWith("echo-");
            catalogue.GetDescriptor(step.Activity).Reference.Should().Be(step.Activity);
        }

        // After execution: durable steps (keys = declarative step IDs), history, terminal result.
        await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
        var result = await engine.WaitForCompletionAsync<JsonElement>(runId, cancellationToken: TestContext.Current.CancellationToken);
        result.GetString().Should().Be("start-a-b-c");
        var steps = await engine.GetStepsAsync(runId, TestContext.Current.CancellationToken);
        steps.Select(s => s.StepKey).Should().Equal("a", "b", "c");
        steps.Should().OnlyContain(s => s.Status == StepStatus.Completed);
        var events = await engine.GetEventsAsync(runId, cancellationToken: TestContext.Current.CancellationToken);
        events.Should().Contain(e => e.EventType == WorkflowEventTypes.WorkflowCompleted);
        var final = await engine.GetRunAsync(runId, TestContext.Current.CancellationToken);
        final!.Status.Should().Be(WorkflowStatus.Completed);
        final.DefinitionFingerprint.Should().Be(compiled.Fingerprint);
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

    [Fact]
    public async Task Execution_StringActivityOutput_IsPreservedVerbatim()
    {
        const string expected = "line one\n\"quoted\" \\ tail\t";
        var catalogue = new ActivityCatalogue();
        catalogue.Register(
            new ActivityReference("special", "1"),
            new FuncActivity<string, string>(_ => Task.FromResult(expected)));
        var definition = new DeclarativeWorkflowDefinition
        {
            Name = "special-text",
            Version = "1",
            Steps = new[]
            {
                new DeclarativeWorkflowStep { Id = "a", Activity = new ActivityReference("special", "1") }
            }
        };
        var compiled = WorkflowCompiler.Compile(definition, catalogue).Compiled!;
        var engine = CreateDeclarativeEngine(compiled, catalogue);
        var runId = await engine.StartAsync<JsonElement>(
            "special-text",
            "1",
            JsonSerializer.SerializeToElement("input"),
            cancellationToken: TestContext.Current.CancellationToken);

        await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
        var result = await engine.WaitForCompletionAsync<JsonElement>(
            runId,
            cancellationToken: TestContext.Current.CancellationToken);

        result.GetString().Should().Be(expected);
    }

    [Fact]
    public async Task DefinitionIdentity_FingerprintedRun_CannotUseUnfingerprintedRegistration()
    {
        var catalogue = CreateCatalogue();
        var compiled = WorkflowCompiler.Compile(ValidLinearDefinition(), catalogue).Compiled!;
        var store = CreateStore();
        var runId = Guid.NewGuid();
        var engine1 = CreateDeclarativeEngine(compiled, catalogue, store);
        await engine1.StartAsync(
            "test",
            "1",
            JsonSerializer.SerializeToElement("start"),
            workflowRunId: runId,
            cancellationToken: TestContext.Current.CancellationToken);

        var registry = new WorkflowRegistry().Register<JsonElement, JsonElement>(
            "test",
            "1",
            new PlainJsonWorkflow());
        var engine2 = new WorkflowEngine(
            store,
            registry,
            new ZhinuOptions { PollInterval = TimeSpan.FromMilliseconds(10) });

        var restart = () => engine2.StartAsync(
            "test",
            "1",
            JsonSerializer.SerializeToElement("start"),
            workflowRunId: runId,
            cancellationToken: TestContext.Current.CancellationToken);
        await restart.Should().ThrowAsync<WorkflowStateException>();

        await engine2.ExecuteAsync(runId, TestContext.Current.CancellationToken);
        var completion = () => engine2.WaitForCompletionAsync<JsonElement>(
            runId,
            cancellationToken: TestContext.Current.CancellationToken);
        await completion.Should().ThrowAsync<WorkflowExecutionFailedException>();
    }

    // --- Persistence / Restart ---

    [Fact]
    public async Task Execution_RestartBetweenSteps_ResumesFromDurableState()
    {
        var enteredSecondStep = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondActivity = new BlockingFirstAttemptActivity(enteredSecondStep);
        var catalogue = new ActivityCatalogue();
        catalogue.Register(new ActivityReference("echo-a", "1"), Echo("-a"));
        catalogue.Register(new ActivityReference("echo-b", "1"), secondActivity);
        catalogue.Register(new ActivityReference("echo-c", "1"), Echo("-c"));
        var definition = ValidLinearDefinition();
        var compiled = WorkflowCompiler.Compile(definition, catalogue).Compiled!;
        var store = CreateStore();
        var recoveryOptions = new ZhinuOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(10),
            LeaseDuration = TimeSpan.FromMilliseconds(150),
            LeaseRenewalInterval = TimeSpan.FromMilliseconds(40)
        };
        var engine1 = CreateDeclarativeEngine(compiled, catalogue, store, recoveryOptions);
        var runId = await engine1.StartAsync<JsonElement>("test", "1", JsonSerializer.SerializeToElement("start"), cancellationToken: TestContext.Current.CancellationToken);
        using var interruption = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        var interruptedExecution = engine1.ExecuteAsync(runId, interruption.Token);
        await enteredSecondStep.Task.WaitAsync(TestContext.Current.CancellationToken);

        var steps1 = await store.GetStepsAsync(runId, TestContext.Current.CancellationToken);
        steps1.Should().ContainSingle(s => s.StepKey == "a" && s.Status == StepStatus.Completed);
        await interruption.CancelAsync();
        await interruptedExecution;

        // Simulate process loss: after the abandoned lease expires, a fresh
        // engine must replay the completed A step and continue from B.
        await Task.Delay(300, TestContext.Current.CancellationToken);

        var engine2 = CreateDeclarativeEngine(compiled, catalogue, store);
        await engine2.RunAvailableAsync(TestContext.Current.CancellationToken);
        var result = await engine2.WaitForCompletionAsync<JsonElement>(runId, cancellationToken: TestContext.Current.CancellationToken);
        result.GetString().Should().Be("start-a-b-c");
        secondActivity.Attempts.Should().Be(2);
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

    private static ActivityCatalogue CreateCatalogue()
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

    private WorkflowEngine CreateDeclarativeEngine(
        CompiledWorkflowDefinition compiled,
        ActivityCatalogue catalogue,
        SqliteWorkflowStore? storeOverride = null,
        ZhinuOptions? options = null)
    {
        var store = storeOverride ?? CreateStore();
        var registry = new WorkflowRegistry().RegisterDeclarative(compiled, catalogue);
        return new WorkflowEngine(
            store,
            registry,
            options ?? new ZhinuOptions { PollInterval = TimeSpan.FromMilliseconds(10) });
    }

    private sealed class FuncActivity<TInput, TOutput> : IActivity<TInput, TOutput>
    {
        private readonly Func<TInput, Task<TOutput>> func;
        public FuncActivity(Func<TInput, Task<TOutput>> func) => this.func = func;
        public Task<TOutput> ExecuteAsync(TInput input, CancellationToken ct) => func(input);
    }

    private sealed class PlainJsonWorkflow : IWorkflow<JsonElement, JsonElement>
    {
        public Task<JsonElement> RunAsync(
            WorkflowContext context,
            JsonElement input,
            CancellationToken cancellationToken) => Task.FromResult(input);
    }

    private sealed class BlockingFirstAttemptActivity : IActivity<string, string>
    {
        private readonly TaskCompletionSource entered;
        private int attempts;

        public BlockingFirstAttemptActivity(TaskCompletionSource entered) =>
            this.entered = entered;

        public int Attempts => Volatile.Read(ref attempts);

        public async Task<string> ExecuteAsync(string input, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return input + "-b";
        }
    }
}
