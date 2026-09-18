using FluentAssertions;

namespace Penghou.Zhinu.Sqlite.Tests;

/// <summary>
/// Mutation spike (Guyabano planning-to-execution): a completed v1 run with
/// steps plan-a/plan-b migrates to v2, which keeps both steps identical and
/// adds exec-c. Completed work must be preserved; only the new node runs.
/// </summary>
public sealed class MutationSpikeTests : WorkflowEngineTestBase
{
    [Fact]
    public async Task ForkToVersion_PreservesCompletedPrefixAndRunsOnlyNewNodes()
    {
        var v1 = new PlanOnlyWorkflow();
        var v2 = new PlanPlusExecuteWorkflow();
        var registry = new WorkflowRegistry()
            .Register("mutate-spike", "1", v1)
            .Register("mutate-spike", "2", v2);
        var engine = CreateEngine(registry);
        var sourceResult = await engine.RunAsync<string, string>(
            "mutate-spike",
            "1",
            "goal",
            cancellationToken: TestContext.Current.CancellationToken);
        sourceResult.Should().Be("B[A(goal)]");
        var source = (await engine.GetRunAsync(v1.RunId, TestContext.Current.CancellationToken))!;

        var migratedId = await engine.ForkAsync(
            source.Id,
            "exec-c",
            new ForkRunOptions
            {
                TargetWorkflowVersion = "2",
                Actor = "spike",
                Reason = "planning complete; start implementation",
            },
            TestContext.Current.CancellationToken);

        var migrated = await engine.GetRunAsync(migratedId, TestContext.Current.CancellationToken);
        migrated!.WorkflowVersion.Should().Be("2");
        migrated.SourceRunId.Should().Be(source.Id);

        var migrateEngine = CreateEngine(registry);
        await migrateEngine.ExecuteAsync(migratedId, TestContext.Current.CancellationToken);
        var result = await migrateEngine.WaitForCompletionAsync<string>(
            migratedId,
            cancellationToken: TestContext.Current.CancellationToken);

        result.Should().Be("C{B[A(goal)]}");
        v1.ACalls.Should().Be(1);
        v1.BCalls.Should().Be(1);
        v2.ACalls.Should().Be(0);
        v2.BCalls.Should().Be(0);
        v2.CCalls.Should().Be(1);

        var completed = await migrateEngine.GetRunAsync(migratedId, TestContext.Current.CancellationToken);
        completed!.Status.Should().Be(WorkflowStatus.Completed);
        var progress = await migrateEngine.GetRunProgressAsync(
            migratedId,
            cancellationToken: TestContext.Current.CancellationToken);
        progress!.SourceRun!.Id.Should().Be(source.Id);
        progress.SourceLineage.Should().ContainSingle()
            .Which.Id.Should().Be(source.Id);
    }

    [Fact]
    public async Task ForkToVersion_RejectsContractChange()
    {
        var v1 = new PlanOnlyWorkflow();
        var v2int = new PlanOnlyIntWorkflow();
        var registry = new WorkflowRegistry()
            .Register("mutate-spike", "1", v1)
            .Register("mutate-spike", "int", v2int);
        var engine = CreateEngine(registry);
        await engine.RunAsync<string, string>(
            "mutate-spike",
            "1",
            "goal",
            cancellationToken: TestContext.Current.CancellationToken);
        var source = (await engine.GetRunAsync(v1.RunId, TestContext.Current.CancellationToken))!;

        var act = () => engine.ForkAsync(
            source.Id,
            "exec-c",
            new ForkRunOptions { TargetWorkflowVersion = "int" },
            TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<WorkflowStateException>();
        var unchanged = await engine.GetRunAsync(source.Id, TestContext.Current.CancellationToken);
        unchanged!.Status.Should().Be(WorkflowStatus.Completed);
    }

    private sealed class PlanOnlyIntWorkflow : IWorkflow<string, int>
    {
        public async Task<int> RunAsync(
            WorkflowContext context,
            string input,
            CancellationToken cancellationToken)
        {
            await context.StepAsync(
                "plan-a",
                input,
                (value, _) => Task.FromResult($"A({value})"),
                cancellationToken: cancellationToken);
            return 42;
        }
    }

    private sealed class PlanOnlyWorkflow : IWorkflow<string, string>
    {
        public int ACalls;
        public int BCalls;
        public Guid RunId { get; private set; }

        public async Task<string> RunAsync(
            WorkflowContext context,
            string input,
            CancellationToken cancellationToken)
        {
            RunId = context.WorkflowRunId;
            var a = await context.StepAsync(
                "plan-a",
                input,
                (value, _) =>
                {
                    ACalls++;
                    return Task.FromResult($"A({value})");
                },
                cancellationToken: cancellationToken);
            string b;
            using (context.DependsOn("plan-a"))
            {
                b = await context.StepAsync(
                    "plan-b",
                    a,
                    (value, _) =>
                    {
                        BCalls++;
                        return Task.FromResult($"B[{value}]");
                    },
                    cancellationToken: cancellationToken);
            }
            return b;
        }
    }

    private sealed class PlanPlusExecuteWorkflow : IWorkflow<string, string>
    {
        public int ACalls;
        public int BCalls;
        public int CCalls;

        public async Task<string> RunAsync(
            WorkflowContext context,
            string input,
            CancellationToken cancellationToken)
        {
            var a = await context.StepAsync(
                "plan-a",
                input,
                (value, _) =>
                {
                    ACalls++;
                    return Task.FromResult($"A({value})");
                },
                cancellationToken: cancellationToken);
            string b;
            using (context.DependsOn("plan-a"))
            {
                b = await context.StepAsync(
                    "plan-b",
                    a,
                    (value, _) =>
                    {
                        BCalls++;
                        return Task.FromResult($"B[{value}]");
                    },
                    cancellationToken: cancellationToken);
                using (context.DependsOn("plan-b"))
                {
                    return await context.StepAsync(
                        "exec-c",
                        b,
                        (value, _) =>
                        {
                            CCalls++;
                            return Task.FromResult($"C{{{value}}}");
                        },
                        cancellationToken: cancellationToken);
                }
            }
        }
    }
}
