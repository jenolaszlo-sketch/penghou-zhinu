using FluentAssertions;
using System.Diagnostics;
using Penghou.Zhinu;

namespace Penghou.Zhinu.Sqlite.Tests;

/// <summary>
/// A child run started by a parent inherits a durable W3C trace identity that
/// survives a process boundary: a second process reopens the database, with no
/// inherited Activity context, and reads the child's exact trace id. Trace
/// continuity is correlation only - the child keeps its own run and step
/// identities, and unrelated runs do not share the trace.
/// </summary>
public sealed class ChildTraceContinuityTests : WorkflowEngineTestBase
{
    [Fact]
    public async Task ChildInheritsParentTraceAcrossProcessBoundary()
    {
        var ct = TestContext.Current.CancellationToken;
        var databasePath = Path.Combine(root, "trace.db");
        var store = new SqliteWorkflowStore(new ZhinuSqliteOptions
        {
            DatabasePath = databasePath,
            BusyTimeout = TimeSpan.FromSeconds(5),
            Pooling = false
        });
        var registry = new WorkflowRegistry()
            .Register("parent", "1", new ParentWorkflow())
            .Register("child", "1", new ChildWorkflow())
            .Register("unrelated", "1", new UnrelatedWorkflow());

        Guid parentId;
        Guid childId;
        string parentTrace;
        string unrelatedTrace;
        IReadOnlyList<Guid> parentStepIds;
        IReadOnlyList<Guid> childStepIds;

        await using (var engine = new WorkflowEngine(
            store,
            registry,
            new ZhinuOptions { PollInterval = TimeSpan.FromMilliseconds(10) }))
        {
            parentId = await engine.StartAsync("parent", "1", "x", cancellationToken: ct);
            await engine.ExecuteAsync(parentId, ct);
            await engine.WaitForCompletionAsync<string>(parentId, cancellationToken: ct);

            var unrelatedId = await engine.StartAsync("unrelated", "1", "x", cancellationToken: ct);
            await engine.ExecuteAsync(unrelatedId, ct);
            await engine.WaitForCompletionAsync<string>(unrelatedId, cancellationToken: ct);

            var subtree = await store.GetRunSubtreeAsync(parentId, 5, ct);
            var child = subtree.Single(run => run.ParentRunId == parentId);
            childId = child.Id;

            parentTrace = (await store.GetRunAsync(parentId, ct))!.TraceId!;
            unrelatedTrace = (await store.GetRunAsync(unrelatedId, ct))!.TraceId!;
            parentStepIds = (await store.GetStepsAsync(parentId, ct)).Select(step => step.Id).ToList();
            childStepIds = (await store.GetStepsAsync(childId, ct)).Select(step => step.Id).ToList();

            // Correlation is shared; execution identity is not.
            parentTrace.Should().NotBeNullOrEmpty();
            child.TraceId.Should().Be(parentTrace);
            parentId.Should().NotBe(childId);
            unrelatedTrace.Should().NotBe(parentTrace);
        }

        parentStepIds.Should().NotBeEmpty();
        childStepIds.Should().NotBeEmpty();
        parentStepIds.Should().NotIntersectWith(childStepIds);

        // Process B: reopen the database with no in-process Activity and read the
        // child's durable trace id.
        var outputPath = Path.Combine(root, "trace.out");
        var processStart = new ProcessStartInfo
        {
            FileName = "dotnet",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        var probeAssemblyPath = GetProcessProbeAssemblyPath();
        File.Exists(probeAssemblyPath).Should().BeTrue(probeAssemblyPath);
        processStart.ArgumentList.Add(probeAssemblyPath);
        processStart.ArgumentList.Add("--zhinu-trace-probe");
        processStart.ArgumentList.Add(databasePath);
        processStart.ArgumentList.Add(childId.ToString("D"));
        processStart.ArgumentList.Add(outputPath);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var process = Process.Start(processStart);
        process.Should().NotBeNull();
        await process!.WaitForExitAsync(timeout.Token);
        process.ExitCode.Should().Be(0);
        var reopenedTrace = (await File.ReadAllTextAsync(outputPath, timeout.Token)).Trim();
        reopenedTrace.Should().Be(parentTrace);
    }

    private static string GetProcessProbeAssemblyPath()
    {
        var testOutput = new DirectoryInfo(AppContext.BaseDirectory);
        var targetFramework = testOutput.Name;
        var configuration = testOutput.Parent?.Name ?? "Debug";
        var testsRoot = testOutput.Parent?.Parent?.Parent?.Parent?.FullName ??
            throw new InvalidOperationException("Could not resolve the test output root.");
        return Path.Combine(
            testsRoot,
            "Penghou.Zhinu.Sqlite.ProcessProbe",
            "bin",
            configuration,
            targetFramework,
            "Penghou.Zhinu.Sqlite.ProcessProbe.dll");
    }

    private sealed class ParentWorkflow : IWorkflow<string, string>
    {
        public Task<string> RunAsync(WorkflowContext context, string input, CancellationToken ct) =>
            context.StartChildAsync<string, string>("child", "child", "1", input, ct);
    }

    private sealed class ChildWorkflow : IWorkflow<string, string>
    {
        public Task<string> RunAsync(WorkflowContext context, string input, CancellationToken ct) =>
            context.StepAsync("child-step", input, (value, _) => Task.FromResult($"child:{value}"), cancellationToken: ct);
    }

    private sealed class UnrelatedWorkflow : IWorkflow<string, string>
    {
        public Task<string> RunAsync(WorkflowContext context, string input, CancellationToken ct) =>
            context.StepAsync("only", input, (value, _) => Task.FromResult(value), cancellationToken: ct);
    }
}
