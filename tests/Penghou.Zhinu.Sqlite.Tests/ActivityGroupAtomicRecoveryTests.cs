using System.Diagnostics;
using FluentAssertions;
using Microsoft.Data.Sqlite;

namespace Penghou.Zhinu.Sqlite.Tests;

public sealed class ActivityGroupAtomicRecoveryTests : WorkflowEngineTestBase
{
    [Theory]
    [InlineData("manifest-install")]
    [InlineData("group-claim")]
    public async Task HardProcessExitInsideGroupTransaction_RecoversFromAtomicBoundary(string mode)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var databasePath = Path.Combine(root, $"atomic-group-{mode}.db");
        var runId = Guid.NewGuid();
        var runIdPath = Path.Combine(root, $"atomic-group-{mode}.run");
        var processStart = new ProcessStartInfo
        {
            FileName = "dotnet",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        processStart.ArgumentList.Add(GetProcessProbeAssemblyPath());
        processStart.ArgumentList.Add("--zhinu-group-atomic-probe");
        processStart.ArgumentList.Add(databasePath);
        processStart.ArgumentList.Add(runId.ToString("D"));
        processStart.ArgumentList.Add(runIdPath);
        processStart.ArgumentList.Add(mode);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        using var process = Process.Start(processStart);
        process.Should().NotBeNull();
        await WaitUntilAsync(() => Task.FromResult(File.Exists(runIdPath)), timeout.Token);
        await process!.WaitForExitAsync(timeout.Token);
        process.ExitCode.Should().Be(93);
        (await File.ReadAllTextAsync(runIdPath, timeout.Token)).Should().Be(runId.ToString("D"));

        var store = new SqliteWorkflowStore(new ZhinuSqliteOptions
        {
            DatabasePath = databasePath,
            BusyTimeout = TimeSpan.FromSeconds(5),
            Pooling = false
        });
        var manifest = CreateManifest();
        var persistedRun = await store.GetRunAsync(runId, timeout.Token);
        var persistedManifest = await store.GetGroupManifestAsync(runId, timeout.Token);
        var events = await store.GetEventsAsync(runId, 0, 100, timeout.Token);

        if (mode == "manifest-install")
        {
            persistedRun.Should().BeNull();
            persistedManifest.Should().BeNull();
            events.Should().BeEmpty();
            return;
        }

        persistedRun.Should().NotBeNull();
        persistedManifest.Should().NotBeNull();
        persistedManifest!.CanonicalJson.Should().Be(manifest.CanonicalJson);
        events.Should().Contain(item => item.EventType == WorkflowEventTypes.WorkflowStarted);

        var rootOccurrence = ActivityGroupOccurrence.Create(runId, manifest, "root", null, "[]");
        var iterationScope = DurableLoopScope.Root("reviewCycle").Iteration(1);
        var iterationOccurrence = ActivityGroupOccurrence.Create(
            runId,
            manifest,
            "iteration",
            rootOccurrence,
            ActivityGroupOccurrence.Coordinate(iterationScope));
        var interruptedSteps = await store.GetStepsAsync(runId, timeout.Token);
        interruptedSteps.Should().NotContain(item => item.StepKey == "$loop/reviewCycle/1/body/work");
        (await store.GetGroupOccurrenceAsync(runId, rootOccurrence.GroupOccurrenceId, timeout.Token))
            .Should().BeNull();
        (await store.GetGroupOccurrenceAsync(runId, iterationOccurrence.GroupOccurrenceId, timeout.Token))
            .Should().BeNull();

        await DropTriggerAsync(databasePath, "zhinu_hard_exit_group_claim");
        await store.RecoverExpiredLeasesAsync(DateTimeOffset.UtcNow.AddMinutes(1), timeout.Token);
        var workflow = new RecoveryAtomicGroupWorkflow();
        await using var engine = new WorkflowEngine(
            store,
            new WorkflowRegistry().Register(
                new WorkflowRegistration<string, string>(
                    new WorkflowDefinition { Name = "process-group-atomic", Version = "1" },
                    () => workflow,
                    manifest)),
            new ZhinuOptions
            {
                PollInterval = TimeSpan.FromMilliseconds(10),
                LeaseDuration = TimeSpan.FromSeconds(2),
                LeaseRenewalInterval = TimeSpan.FromMilliseconds(600)
            });
        await engine.ExecuteAsync(runId, timeout.Token);
        var output = await engine.WaitForCompletionAsync<string>(runId, cancellationToken: timeout.Token);

        output.Should().Be("1");
        workflow.BodyOperationCalls.Should().Be(1);
        var currentSteps = await store.GetStepsAsync(runId, timeout.Token);
        var bodyStep = currentSteps.Single(item => item.StepKey == "$loop/reviewCycle/1/body/work");
        var membership = await store.GetStepGroupMembershipAsync(bodyStep.Id, timeout.Token);
        membership.Should().NotBeNull();
        membership!.GroupOccurrenceId.Should().Be(iterationOccurrence.GroupOccurrenceId);
        (await store.GetGroupOccurrenceAsync(runId, rootOccurrence.GroupOccurrenceId, timeout.Token))
            .Should().NotBeNull();
        (await store.GetGroupOccurrenceAsync(runId, iterationOccurrence.GroupOccurrenceId, timeout.Token))
            .Should().NotBeNull();
    }

    private static async Task DropTriggerAsync(string databasePath, string triggerName)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"DROP TRIGGER IF EXISTS {triggerName};";
        await command.ExecuteNonQueryAsync();
    }

    private static ActivityGroupManifest CreateManifest() => ActivityGroupManifest.Create(new[]
    {
        new ActivityGroupDefinition("root", ActivityGroupKind.Logical, "Root", childOrdinal: 0),
        new ActivityGroupDefinition("iteration", ActivityGroupKind.Logical, "Iteration", "root")
    });

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

    private sealed class RecoveryAtomicGroupWorkflow : IWorkflow<string, string>
    {
        private int bodyOperationCalls;

        public int BodyOperationCalls => Volatile.Read(ref bodyOperationCalls);

        public async Task<string> RunAsync(
            WorkflowContext context,
            string input,
            CancellationToken cancellationToken)
        {
            var root = context.GetActivityGroup("root");
            var result = await context.LoopAsync(
                "reviewCycle",
                0,
                state => state < 1,
                async (iteration, token) =>
                {
                    var occurrence = iteration.GetActivityGroup("iteration", root);
                    var next = await iteration.StepAsync(
                        "work",
                        iteration.State,
                        (state, _, _) =>
                        {
                            Interlocked.Increment(ref bodyOperationCalls);
                            return Task.FromResult(state + 1);
                        },
                        new StepOptions
                        {
                            Group = new ActivityGroupStepBinding(
                                new ActivityGroupMembership("iteration"),
                                occurrence)
                        },
                        token).ConfigureAwait(false);
                    return iteration.Continue(next);
                },
                new LoopOptions(maxIterations: 1),
                cancellationToken).ConfigureAwait(false);
            return result.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
