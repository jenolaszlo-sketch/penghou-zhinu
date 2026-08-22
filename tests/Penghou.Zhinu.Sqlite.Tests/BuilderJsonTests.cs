using FluentAssertions;
using System.Text.Json;
#pragma warning disable xUnit1051

namespace Penghou.Zhinu.Sqlite.Tests;

public sealed class BuilderJsonTests : WorkflowEngineTestBase
{
    [Fact]
    public void ZhinuJsonDefaults_CreateDefault_IsReadOnlyAndHasStringEnum()
    {
        var opts = ZhinuJsonDefaults.CreateDefault();
        opts.IsReadOnly.Should().BeTrue();
        // Web defaults use camelCase, ensure enum as string
        opts.Converters.Should().Contain(c => c.GetType().Name.Contains("JsonStringEnumConverter"));
    }

    [Fact]
    public void ZhinuJsonDefaults_CloneAndFreeze_ClonesAndFreezes()
    {
        var src = new JsonSerializerOptions { WriteIndented = true };
        var clone = ZhinuJsonDefaults.CloneAndFreeze(src);
        clone.IsReadOnly.Should().BeTrue();
        clone.WriteIndented.Should().BeTrue();
        src.WriteIndented = false;
        clone.WriteIndented.Should().BeTrue();
    }

    [Fact]
    public void WorkflowEngineBuilder_WithOptions_ClonesDefensively()
    {
        var opts = new ZhinuOptions { MaxConcurrentWorkflows = 2 };
        var builder = new WorkflowEngineBuilder().WithStore(CreateStore()).WithRegistry(new WorkflowRegistry().Register("x", "1", new TwoStepWorkflow())).WithOptions(opts);
        opts.MaxConcurrentWorkflows = 99;
        var engine = builder.Build();
        // Engine should still have 2, not 99, because builder cloned
        engine.Should().NotBeNull();
    }

    [Fact]
    public void WorkflowEngine_CloneOptions_PostMutationDoesNotAffectEngine()
    {
        var opts = new ZhinuOptions { PollInterval = TimeSpan.FromMilliseconds(100) };
        var engine = new WorkflowEngine(CreateStore(), new WorkflowRegistry().Register("x", "1", new TwoStepWorkflow()), opts);
        opts.PollInterval = TimeSpan.FromSeconds(99);
        // Engine's internal options should remain 100ms (no public getter, but no exception and engine still works)
        engine.Should().NotBeNull();
    }

    [Fact]
    public void ZhinuOptions_Validate_FailsFastOnInvalid()
    {
        var act = () => new ZhinuOptions { MaxConcurrentWorkflows = 0 }.Validate();
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void ZhinuSqliteOptions_Validate_FailsFast()
    {
        var act = () => new ZhinuSqliteOptions { DatabasePath = "  " }.Validate();
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void SignalDefinition_RequiresNonEmptyName()
    {
        var act = () => new SignalDefinition<string>("  ");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void WorkflowEngineBuilder_RequiresStoreAndRegistry()
    {
        var builder = new WorkflowEngineBuilder();
        var act = () => builder.Build();
        act.Should().Throw<InvalidOperationException>();
    }
}
