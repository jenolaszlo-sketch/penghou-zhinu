using FluentAssertions;

namespace Penghou.Zhinu.Sqlite.Tests;

/// <summary>
/// Lexical dependency scopes stay isolated across concurrent workflow
/// branches: a sibling branch never observes another branch's declarations.
/// </summary>
public sealed class DependencyScopeTests : WorkflowEngineTestBase
{
    [Fact]
    public async Task ParallelDependencyScopes_AreIsolated()
    {
        await using var engine = CreateEngine(new ScopedParallelWorkflow(), "scopes");
        var id = await engine.StartAsync(
            "scopes", "1", "x", cancellationToken: TestContext.Current.CancellationToken);
        await engine.ExecuteAsync(id, TestContext.Current.CancellationToken);
        var edges = await engine.GetDependencyGraphAsync(id, TestContext.Current.CancellationToken);
        edges.Should().NotContain(e => e.StepKey == "result-a" && e.DependsOnStepKey == "source-b");
    }

    [Fact]
    public async Task NestedScopes_RestoreOuterDeclarations()
    {
        await using var engine = CreateEngine(new NestedScopeWorkflow(), "nested-scopes");
        var id = await engine.StartAsync(
            "nested-scopes", "1", "x", cancellationToken: TestContext.Current.CancellationToken);
        await engine.ExecuteAsync(id, TestContext.Current.CancellationToken);
        var edges = await engine.GetDependencyGraphAsync(id, TestContext.Current.CancellationToken);
        edges.Should().Contain(e => e.StepKey == "inner" && e.DependsOnStepKey == "a");
        edges.Should().Contain(e => e.StepKey == "inner" && e.DependsOnStepKey == "b");
        edges.Should().Contain(e => e.StepKey == "outer" && e.DependsOnStepKey == "a");
        edges.Should().NotContain(e => e.StepKey == "outer" && e.DependsOnStepKey == "b");
        edges.Should().NotContain(e => e.StepKey == "plain");
    }

    [Fact]
    public async Task ScopeExit_OnException_RestoresPriorState()
    {
        await using var engine = CreateEngine(new ScopeExceptionWorkflow(), "scope-exception");
        var id = await engine.StartAsync(
            "scope-exception", "1", "x", cancellationToken: TestContext.Current.CancellationToken);
        await engine.ExecuteAsync(id, TestContext.Current.CancellationToken);
        var edges = await engine.GetDependencyGraphAsync(id, TestContext.Current.CancellationToken);
        edges.Should().NotContain(e => e.StepKey == "after" && e.DependsOnStepKey == "leaky");
    }

    private sealed class ScopedParallelWorkflow : IWorkflow<string, string>
    {
        public async Task<string> RunAsync(
            WorkflowContext context, string input, CancellationToken cancellationToken)
        {
            await context.StepAsync(
                "source-a", _ => Task.FromResult(input), cancellationToken: cancellationToken);
            await context.StepAsync(
                "source-b", _ => Task.FromResult(input), cancellationToken: cancellationToken);
            var enteredByB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var doneByA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            async Task BranchA()
            {
                using var scope = context.DependsOn("source-a");
                await enteredByB.Task.WaitAsync(cancellationToken);
                await context.StepAsync(
                    "result-a", _ => Task.FromResult(input), cancellationToken: cancellationToken);
                doneByA.SetResult();
            }
            async Task BranchB()
            {
                using var scope = context.DependsOn("source-b");
                enteredByB.SetResult();
                await doneByA.Task.WaitAsync(cancellationToken);
                await context.StepAsync(
                    "result-b", _ => Task.FromResult(input), cancellationToken: cancellationToken);
            }
            await Task.WhenAll(BranchA(), BranchB());
            return input;
        }
    }

    private sealed class NestedScopeWorkflow : IWorkflow<string, string>
    {
        public async Task<string> RunAsync(
            WorkflowContext context, string input, CancellationToken cancellationToken)
        {
            await context.StepAsync(
                "a", _ => Task.FromResult(input), cancellationToken: cancellationToken);
            using (context.DependsOn("a"))
            {
                await context.StepAsync(
                    "outer", _ => Task.FromResult(input), cancellationToken: cancellationToken);
                using (context.DependsOn("a", "b"))
                {
                    await context.StepAsync(
                        "inner", _ => Task.FromResult(input), cancellationToken: cancellationToken);
                }
            }
            return await context.StepAsync(
                "plain", _ => Task.FromResult(input), cancellationToken: cancellationToken);
        }
    }

    private sealed class ScopeExceptionWorkflow : IWorkflow<string, string>
    {
        public async Task<string> RunAsync(
            WorkflowContext context, string input, CancellationToken cancellationToken)
        {
            try
            {
                using (context.DependsOn("leaky"))
                {
                    throw new InvalidOperationException("boom");
                }
            }
            catch (InvalidOperationException)
            {
            }
            return await context.StepAsync(
                "after", _ => Task.FromResult(input), cancellationToken: cancellationToken);
        }
    }
}
