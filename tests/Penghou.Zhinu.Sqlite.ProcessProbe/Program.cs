using System.Globalization;
using Penghou.Zhinu.Sqlite.Tests;

namespace Penghou.Zhinu.Sqlite.ProcessProbe;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length != 4 || args[0] != "--zhinu-loop-probe")
            return 2;

        var databasePath = args[1];
        var runIdPath = args[2];
        var mode = args[3];
        var faultPoint = mode switch
        {
            "before" => FaultInjectingWorkflowStore.BeforeStepCompletionCommit,
            "after" => FaultInjectingWorkflowStore.AfterStepCompletionCommit,
            _ => throw new ArgumentException($"Unknown probe mode '{mode}'.", nameof(args))
        };
        var store = new FaultInjectingWorkflowStore(
            new SqliteWorkflowStore(
                new ZhinuSqliteOptions
                {
                    DatabasePath = databasePath,
                    BusyTimeout = TimeSpan.FromSeconds(5),
                    Pooling = false
                }));
        store.ArmProcessTerminationAtOutput(faultPoint, "\"Kind\"");
        // The process probe intentionally arms the decorator before any
        // execution; every step completion must pass through it.
        const string workflowName = "process-loop-boundary";
        var engine = new WorkflowEngine(
            store,
            new WorkflowRegistry().Register(
                workflowName,
                "1",
                new ProbeLoopWorkflow()),
            new ZhinuOptions
            {
                PollInterval = TimeSpan.FromMilliseconds(10),
                LeaseDuration = TimeSpan.FromSeconds(2),
                LeaseRenewalInterval = TimeSpan.FromMilliseconds(600)
            });
        var runId = await engine.StartAsync(
            workflowName,
            "1",
            "probe",
            cancellationToken: CancellationToken.None).ConfigureAwait(false);
        File.WriteAllText(runIdPath, runId.ToString("D"));
        await engine.ExecuteAsync(runId, CancellationToken.None).ConfigureAwait(false);
        return 0;
    }

    private sealed class ProbeLoopWorkflow : IWorkflow<string, string>
    {
        public async Task<string> RunAsync(
            WorkflowContext context,
            string input,
            CancellationToken cancellationToken)
        {
            var result = await context.LoopAsync(
                "state",
                0,
                state => state < 1,
                async (iteration, token) =>
                {
                    var next = await iteration.StepAsync(
                        "increment",
                        iteration.State,
                        (state, _, _) => Task.FromResult(state + 1),
                        cancellationToken: token).ConfigureAwait(false);
                    return iteration.Continue(next);
                },
                new LoopOptions(maxIterations: 2),
                cancellationToken).ConfigureAwait(false);
            return result.ToString(CultureInfo.InvariantCulture);
        }
    }
}
