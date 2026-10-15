using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;

namespace Penghou.Zhinu.Sqlite.Tests;

public sealed class ActivityGroupRecoveryTests : WorkflowEngineTestBase
{
    [Theory]
    [InlineData("before")]
    [InlineData("after")]
    public async Task ProcessLoss_PreservesRepeatedLoopOccurrencesAndMembership(string mode)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var databasePath = Path.Combine(root, $"group-process-{mode}.db");
        var runIdPath = Path.Combine(root, $"group-process-{mode}.run");
        var processStart = new ProcessStartInfo
        {
            FileName = "dotnet",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        processStart.ArgumentList.Add(GetProcessProbeAssemblyPath());
        processStart.ArgumentList.Add("--zhinu-group-probe");
        processStart.ArgumentList.Add(databasePath);
        processStart.ArgumentList.Add(runIdPath);
        processStart.ArgumentList.Add(mode);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        using var process = Process.Start(processStart);
        process.Should().NotBeNull();
        await WaitUntilAsync(() => Task.FromResult(File.Exists(runIdPath)), timeout.Token);
        await process!.WaitForExitAsync(timeout.Token);
        process.ExitCode.Should().NotBe(0);

        var runId = Guid.Parse(await File.ReadAllTextAsync(runIdPath, timeout.Token));
        var store = new SqliteWorkflowStore(new ZhinuSqliteOptions
        {
            DatabasePath = databasePath,
            BusyTimeout = TimeSpan.FromSeconds(5),
            Pooling = false
        });
        var interruptedRun = await store.GetRunAsync(runId, timeout.Token);
        interruptedRun.Should().NotBeNull();
        interruptedRun!.LeaseExpiresAt.Should().NotBeNull();
        var leaseDelay = interruptedRun.LeaseExpiresAt!.Value - DateTimeOffset.UtcNow;
        if (leaseDelay > TimeSpan.Zero)
            await Task.Delay(leaseDelay + TimeSpan.FromMilliseconds(50), timeout.Token);
        await store.RecoverExpiredLeasesAsync(DateTimeOffset.UtcNow, timeout.Token);

        var beforeRecovery = await CaptureGroupSnapshotAsync(store, runId, timeout.Token);
        beforeRecovery.Memberships.Select(item => item.StepKey)
            .Should().ContainInOrder(
                "$loop/reviewCycle/1/body/work",
                "$loop/reviewCycle/2/body/work");
        beforeRecovery.Occurrences.Select(item => item.ExecutionCoordinate)
            .Should().Contain(
                "[{\"loop\":\"reviewCycle\",\"iteration\":1}]",
                "[{\"loop\":\"reviewCycle\",\"iteration\":2}]");

        var recoveryWorkflow = new RecoveryGroupWorkflow();
        await using var recovery = new WorkflowEngine(
            store,
            new WorkflowRegistry().Register(
                new WorkflowRegistration<string, string>(
                    new WorkflowDefinition { Name = "process-group-boundary", Version = "1" },
                    () => recoveryWorkflow,
                    CreateManifest())),
            new ZhinuOptions
            {
                PollInterval = TimeSpan.FromMilliseconds(10),
                LeaseDuration = TimeSpan.FromSeconds(2),
                LeaseRenewalInterval = TimeSpan.FromMilliseconds(600)
            });
        await recovery.ExecuteAsync(runId, timeout.Token);
        var output = await recovery.WaitForCompletionAsync<string>(
            runId,
            cancellationToken: timeout.Token);
        var afterRecovery = await CaptureGroupSnapshotAsync(store, runId, timeout.Token);

        output.Should().Be("2");
        recoveryWorkflow.BodyOperationCalls.Should().Be(0);
        afterRecovery.Bytes.Should().Equal(beforeRecovery.Bytes);
    }

    private static async Task<GroupSnapshot> CaptureGroupSnapshotAsync(
        SqliteWorkflowStore store,
        Guid runId,
        CancellationToken cancellationToken)
    {
        var manifest = await store.GetGroupManifestAsync(runId, cancellationToken);
        manifest.Should().NotBeNull();
        var steps = await store.GetStepsAsync(runId, cancellationToken);
        var memberships = new List<MembershipSnapshot>();
        var occurrences = new Dictionary<string, OccurrenceSnapshot>(StringComparer.Ordinal);
        foreach (var step in steps.OrderBy(item => item.StepKey, StringComparer.Ordinal))
        {
            var membership = await store.GetStepGroupMembershipAsync(step.Id, cancellationToken);
            if (membership is null)
                continue;
            memberships.Add(new MembershipSnapshot(
                membership.StepId,
                membership.StepKey,
                membership.StepRevision,
                membership.GroupOccurrenceId,
                membership.Role));
            await AddOccurrenceAndAncestorsAsync(
                store,
                runId,
                membership.GroupOccurrenceId,
                occurrences,
                cancellationToken);
        }

        var snapshot = new GroupSnapshot(
            manifest!.CanonicalJson,
            memberships,
            occurrences.Values.OrderBy(item => item.GroupOccurrenceId, StringComparer.Ordinal).ToArray());
        return snapshot with { Bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot) };
    }

    private static async Task AddOccurrenceAndAncestorsAsync(
        SqliteWorkflowStore store,
        Guid runId,
        string occurrenceId,
        Dictionary<string, OccurrenceSnapshot> occurrences,
        CancellationToken cancellationToken)
    {
        if (occurrences.ContainsKey(occurrenceId))
            return;
        var occurrence = await store.GetGroupOccurrenceAsync(runId, occurrenceId, cancellationToken);
        occurrence.Should().NotBeNull();
        occurrences.Add(occurrenceId, new OccurrenceSnapshot(
            occurrence!.GroupOccurrenceId,
            occurrence.GroupKey,
            occurrence.ParentGroupOccurrenceId,
            occurrence.ExecutionCoordinate));
        if (occurrence.ParentGroupOccurrenceId is { } parentId)
        {
            await AddOccurrenceAndAncestorsAsync(
                store,
                runId,
                parentId,
                occurrences,
                cancellationToken);
        }
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

    private sealed class RecoveryGroupWorkflow : IWorkflow<string, string>
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
                state => state < 2,
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
                new LoopOptions(maxIterations: 2),
                cancellationToken).ConfigureAwait(false);
            return result.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    private sealed record GroupSnapshot(
        string ManifestCanonicalJson,
        IReadOnlyList<MembershipSnapshot> Memberships,
        IReadOnlyList<OccurrenceSnapshot> Occurrences)
    {
        public byte[] Bytes { get; init; } = [];
    }

    private sealed record MembershipSnapshot(
        Guid StepId,
        string StepKey,
        int Revision,
        string GroupOccurrenceId,
        ActivityGroupMemberRole Role);

    private sealed record OccurrenceSnapshot(
        string GroupOccurrenceId,
        string GroupKey,
        string? ParentGroupOccurrenceId,
        string ExecutionCoordinate);
}
