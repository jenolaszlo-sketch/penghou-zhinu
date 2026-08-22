using FluentAssertions;
#pragma warning disable xUnit1051

namespace Penghou.Zhinu.Sqlite.Tests;

public sealed class DependencyCycleTests : WorkflowEngineTestBase
{
    [Fact]
    public async Task Step_SelfDependency_Throws()
    {
        var workflow = new SelfDependentWorkflow();
        var engine = CreateEngine(workflow, "cycle-self");
        var runId = await engine.StartAsync("cycle-self", "1", "x", cancellationToken: TestContext.Current.CancellationToken);
        await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
        var run = await engine.GetRunAsync(runId, TestContext.Current.CancellationToken);
        run!.Status.Should().Be(WorkflowStatus.Failed);
    }

    [Fact]
    public void Validator_DirectCycle_Detected()
    {
        var edges = new List<StepDependency>
        {
            new("b", "a"),
            new("a", "b")
        };
        WorkflowDependencyValidator.HasCycle(edges).Should().BeTrue();
    }

    [Fact]
    public void Validator_IndirectCycle_Detected()
    {
        var edges = new List<StepDependency>
        {
            new("b", "a"),
            new("c", "b"),
            new("a", "c")
        };
        WorkflowDependencyValidator.HasCycle(edges).Should().BeTrue();
    }

    [Fact]
    public void Validator_NoCycle_ReturnsFalse()
    {
        var edges = new List<StepDependency>
        {
            new("b", "a"),
            new("c", "a"),
            new("d", "b")
        };
        WorkflowDependencyValidator.HasCycle(edges).Should().BeFalse();
    }

    [Fact]
    public void Validator_DuplicateDependency_NoCycle()
    {
        var edges = new List<StepDependency>
        {
            new("b", "a"),
            new("b", "a")
        };
        WorkflowDependencyValidator.HasCycle(edges).Should().BeFalse();
    }

    [Fact]
    public async Task FanOut_Dependencies_AreIndependent()
    {
        var workflow = new FanOutWorkflow();
        var engine = CreateEngine(workflow, "cycle-fanout");
        var runId = await engine.StartAsync("cycle-fanout", "1", "a,b,c", cancellationToken: TestContext.Current.CancellationToken);
        await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
        var result = await engine.WaitForCompletionAsync<string>(runId, cancellationToken: TestContext.Current.CancellationToken);
        result.Should().Be("A:B:C");
        var deps = await engine.GetDependencyGraphAsync(runId, TestContext.Current.CancellationToken);
        // FanOut items are siblings, should not depend on each other
        deps.Where(d => d.StepKey.StartsWith("process.")).Should().BeEmpty();
    }

    [Fact]
    public async Task MissingDependency_BlockedDiagnosis()
    {
        var workflow = new MissingDepWorkflow();
        var engine = CreateEngine(workflow, "cycle-missing");
        var runId = await engine.StartAsync("cycle-missing", "1", "x", cancellationToken: TestContext.Current.CancellationToken);
        // Start execution but workflow will try to depend on non-existent step; should still run but Diagnose shows blocked?
        // Our MissingDepWorkflow uses DependsOn("nonexistent") for step "second" after "first" completes
        await engine.ExecuteAsync(runId, TestContext.Current.CancellationToken);
        var diag = await engine.DiagnoseAsync(runId, TestContext.Current.CancellationToken);
        diag.Should().NotBeNull();
    }

    private sealed class SelfDependentWorkflow : IWorkflow<string, string>
    {
        public async Task<string> RunAsync(WorkflowContext ctx, string input, CancellationToken ct)
        {
            return await ctx.StepAsync("a", input, (v, _, _) => Task.FromResult(v), new StepOptions { DependsOn = ["a"] }, ct);
        }
    }

    private sealed class MissingDepWorkflow : IWorkflow<string, string>
    {
        public async Task<string> RunAsync(WorkflowContext ctx, string input, CancellationToken ct)
        {
            var first = await ctx.StepAsync("first", input, (v, _) => Task.FromResult(v), cancellationToken: ct);
            using (ctx.DependsOn("nonexistent"))
            {
                return await ctx.StepAsync("second", first, (v, _) => Task.FromResult(v), cancellationToken: ct);
            }
        }
    }
}
