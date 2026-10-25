using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
using Penghou.Zhinu.Sqlite.ProcessProbe;

namespace Penghou.Zhinu.Sqlite.Tests;

public sealed class ActivityGroupQueryProcessTests(ITestOutputHelper testOutput) : WorkflowEngineTestBase
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FreshProcessReaderReconstructsPersistedNestedGroupsWithoutExecutionState()
    {
        var engine = new WorkflowEngine(CreateStore(), GroupNestedWorkflow.Registry(new GroupNestedWorkflow()),
            new ZhinuOptions { PollInterval = TimeSpan.FromMilliseconds(10) });
        Guid runId;
        await using (engine)
        {
            runId = await engine.StartAsync(GroupNestedWorkflow.Name, "1", "process-query-payload", cancellationToken: Ct);
            await engine.ExecuteAsync(runId, Ct);
            (await engine.WaitForCompletionAsync<string>(runId, cancellationToken: Ct)).Should().Be("3");
        }

        // This token belongs to the test process's reader. The fresh worker below
        // must reject it and issue a new token from its own process-local cache.
        using var originalReader = new WorkflowGroupReader(CreateStore());
        var original = await originalReader.GetRunGroupSnapshotAsync(runId, Ct);
        original.Status.Should().Be(WorkflowGroupReadStatus.Success);
        var receiptPath = Path.Combine(root, "fresh-query-receipt.json");
        var probe = ProcessProbePath();
        var start = new ProcessStartInfo { FileName = "dotnet", UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[]
        {
            probe,
            "--zhinu-group-query-probe",
            Path.Combine(root, "zhinu.db"),
            runId.ToString("D"),
            original.Value!.SnapshotToken,
            receiptPath
        })
            start.ArgumentList.Add(argument);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        using var process = Process.Start(start)!;
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }

        process.ExitCode.Should().Be(0);
        File.Exists(receiptPath).Should().BeTrue();
        using var receipt = JsonDocument.Parse(await File.ReadAllTextAsync(receiptPath, Ct));
        var rootElement = receipt.RootElement;
        rootElement.GetProperty("Scenario").GetString().Should().Be("fresh-process-group-query");
        rootElement.GetProperty("RunId").GetGuid().Should().Be(runId);
        rootElement.GetProperty("PriorProcessTokenStatus").GetString().Should().Be(nameof(WorkflowGroupReadStatus.ResyncRequired));
        rootElement.GetProperty("RootOccurrenceCount").GetInt32().Should().Be(3);
        rootElement.GetProperty("DefinitionKeys").EnumerateArray().Select(item => item.GetString())
            .Should().BeEquivalentTo("outer", "inner", "leaf");

        var occurrences = rootElement.GetProperty("Occurrences").EnumerateArray().ToArray();
        occurrences.Should().HaveCount(15);
        var occurrenceIds = occurrences.Select(item => item.GetProperty("OccurrenceId").GetString()!).ToHashSet(StringComparer.Ordinal);
        occurrenceIds.Should().HaveCount(15);
        var roots = occurrences.Where(item => item.GetProperty("GroupKey").GetString() == "outer").ToArray();
        var inners = occurrences.Where(item => item.GetProperty("GroupKey").GetString() == "inner").ToArray();
        var leaves = occurrences.Where(item => item.GetProperty("GroupKey").GetString() == "leaf").ToArray();
        roots.Should().HaveCount(3).And.OnlyContain(item => item.GetProperty("ParentOccurrenceId").ValueKind == JsonValueKind.Null);
        inners.Should().HaveCount(6).And.OnlyContain(item => occurrenceIds.Contains(item.GetProperty("ParentOccurrenceId").GetString()!));
        leaves.Should().HaveCount(6).And.OnlyContain(item => occurrenceIds.Contains(item.GetProperty("ParentOccurrenceId").GetString()!));

        var members = rootElement.GetProperty("Members").EnumerateArray().ToArray();
        members.Should().HaveCount(15);
        members.Should().OnlyContain(item => occurrenceIds.Contains(item.GetProperty("GroupOccurrenceId").GetString()!));
        members.Select(item => item.GetProperty("StepKey").GetString()).Should().OnlyHaveUniqueItems();
        rootElement.GetProperty("PayloadFieldsAbsent").GetBoolean().Should().BeTrue();
        var states = rootElement.GetProperty("GroupStates").EnumerateArray().ToArray();
        states.Should().HaveCount(15);
        states.Select(state => state.GetProperty("Status").GetString()).Should().OnlyContain(status => status == "Completed");
        states.Where(state => roots.Any(occurrence => occurrence.GetProperty("OccurrenceId").GetString() == state.GetProperty("OccurrenceId").GetString()))
            .Sum(state => state.GetProperty("Work").GetProperty("Known").GetInt32()).Should().Be(15);

        testOutput.WriteLine("GROUP_QUERY_PROCESS_EVIDENCE " + JsonSerializer.Serialize(new
        {
            runId,
            workerExitCode = process.ExitCode,
            priorTokenStatus = rootElement.GetProperty("PriorProcessTokenStatus").GetString(),
            occurrenceCount = occurrences.Length,
            memberCount = members.Length,
            rootCount = roots.Length,
            innerCount = inners.Length,
            leafCount = leaves.Length,
            definitions = rootElement.GetProperty("DefinitionKeys").EnumerateArray().Select(item => item.GetString()).ToArray()
            ,
            aggregateStates = states.Length
        }));
    }

    private static string ProcessProbePath()
    {
        var output = new DirectoryInfo(AppContext.BaseDirectory);
        return Path.Combine(output.Parent!.Parent!.Parent!.Parent!.FullName, "Penghou.Zhinu.Sqlite.ProcessProbe", "bin",
            output.Parent.Name, output.Name, "Penghou.Zhinu.Sqlite.ProcessProbe.dll");
    }
}
