using Penghou.Zhinu;
using Penghou.Zhinu.Declarative;
using Penghou.Zhinu.Sqlite;
using System.Text.Json;

// Direct construction: no hosted loop, no DI. The engine drives only the
// runs you ask it to. This sample exercises typed handles, child workflows,
// typed signals, artifact publication, query, and cancellation.

var store = new SqliteWorkflowStore(new ZhinuSqliteOptions
{
    DatabasePath = Path.Combine(AppContext.BaseDirectory, "data", "zhinu.db")
});
var activityCatalogue = new ActivityCatalogue();
activityCatalogue.Register(
    new ActivityReference("append-reviewed", "1"),
    new AppendActivity("-reviewed"));
var declarativeDefinition = new DeclarativeWorkflowDefinition
{
    Name = "declarative-review",
    Version = "1",
    Steps =
    [
        new DeclarativeWorkflowStep
        {
            Id = "review",
            Activity = new ActivityReference("append-reviewed", "1")
        }
    ]
};
var compilation = WorkflowCompiler.Compile(
    declarativeDefinition,
    activityCatalogue);
var compiledDefinition = compilation.Compiled ?? throw new InvalidOperationException(
    string.Join(Environment.NewLine, compilation.Diagnostics.Select(d => d.Message)));

var registry = new WorkflowRegistry()
    .Register("parent", "1", new ParentWorkflow())
    .Register("thumbnail", "1", new ThumbnailWorkflow())
    .RegisterDeclarative(compiledDefinition, activityCatalogue);
var engine = new WorkflowEngine(
    store,
    registry,
    new ZhinuOptions
    {
        PollInterval = TimeSpan.FromMilliseconds(50),
        LeaseDuration = TimeSpan.FromSeconds(10),
        LeaseRenewalInterval = TimeSpan.FromSeconds(3)
    });

// 1. Typed handle: start, inspect, wait.
WorkflowHandle<string> handle = await engine.StartHandleAsync<string, string>(
    "parent",
    "1",
    "request-42",
    metadata: new { Owner = "demo", CorrelationId = "abc-123" });
Console.WriteLine($"Started {handle.WorkflowRunId:D}.");

var approval = new SignalDefinition<string>("approve");
await handle.SendSignalAsync(approval, "approved");

var execution = engine.ExecuteAsync(handle.WorkflowRunId);
// While the parent waits for the child, show its inbox and live progress.
while (execution.IsCompleted == false)
{
    var signals = await handle.GetSignalsAsync();
    var progress = await handle.GetRunProgressAsync();
    if (signals.Count > 0)
        Console.WriteLine($"  inbox: {signals.Count} signal(s), {signals.Count(s => s.Status == SignalStatus.Consumed)} consumed");
    if (progress is not null)
        Console.WriteLine($"  steps: {progress.CompletedSteps} done / {progress.Steps.Count} total");
    await Task.Delay(200);
}
await execution;

// 2. Result inspection: non-throwing snapshot vs throwing wait.
WorkflowResult<string> snapshot = await handle.GetResultAsync();
Console.WriteLine($"Snapshot: {snapshot.Status} -> {snapshot.Value}");
string result = await handle.WaitAsync();
Console.WriteLine($"WaitAsync: {result}");

// The declarative definition uses the same engine and durability model.
var declarativeRun = await engine.StartAsync(
    "declarative-review",
    "1",
    JsonSerializer.SerializeToElement("plan"));
await engine.ExecuteAsync(declarativeRun);
var declarativeResult = await engine.WaitForCompletionAsync<JsonElement>(
    declarativeRun);
Console.WriteLine($"Declarative: {declarativeResult.GetString()}");

// 3. Artifacts published by the child, queried by cursor.
var artifacts = await handle.QueryArtifactsAsync(
    new ArtifactQuery { ArtifactType = "image/png", Limit = 10 });
foreach (var artifact in artifacts)
    Console.WriteLine($"  artifact {artifact.Name}#{artifact.Revision} -> {artifact.Location}");

// 4. Cancellation is durable and audited: start a run that blocks on a signal
//    that is never delivered, then cancel it.
var cancellable = await engine.StartHandleAsync<string, string>(
    "parent", "1", "cancel-me");
var runaway = engine.ExecuteAsync(cancellable.WorkflowRunId);
await cancellable.CancelAsync("ops", "demo sweep");
try { await runaway; } catch { /* cancellation */ }
var cancelled = await cancellable.GetResultAsync();
Console.WriteLine($"Cancelled run: {cancelled.Status}");

// 5. Bounded inbox: purge consumed signals (delivered audit events remain).
var purged = await handle.PurgeSignalsAsync();
Console.WriteLine($"Purged {purged} consumed signal row(s).");

await engine.DisposeAsync();

internal sealed class ParentWorkflow : IWorkflow<string, string>
{
    private static readonly SignalDefinition<string> Approval =
        new("approve");

    public async Task<string> RunAsync(
        WorkflowContext context,
        string input,
        CancellationToken cancellationToken)
    {
        var plan = await context.StepAsync(
            "plan",
            input,
            (value, _) => Task.FromResult($"plan:{value}"),
            cancellationToken: cancellationToken);
        var decision = await context.WaitForSignalAsync<string>(
            "approval",
            Approval,
            timeout: TimeSpan.FromMinutes(5),
            cancellationToken);
        var thumb = await context.StartChildAsync<string, string>(
            "thumbnail",
            "thumbnail",
            "1",
            $"{plan}:{decision}",
            new ChildRunOptions { InheritMetadata = true },
            cancellationToken);
        return await context.StepAsync(
            "confirm",
            thumb,
            (value, _) => Task.FromResult($"done:{value}"),
            cancellationToken: cancellationToken);
    }
}

internal sealed class ThumbnailWorkflow : IWorkflow<string, string>
{
    public async Task<string> RunAsync(
        WorkflowContext context,
        string input,
        CancellationToken cancellationToken)
    {
        var image = await context.StepAsync<string, string>(
            "render",
            input,
            (value, step, ct) => Task.FromResult($"rendered:{value}"),
            cancellationToken: cancellationToken);
        await context.StepAsync<string, string>(
            "publish",
            image,
            async (value, step, ct) =>
            {
                await step.PublishArtifactAsync(
                    new WorkflowArtifactDescriptor
                    {
                        Name = "thumbnail",
                        ArtifactType = "image/png",
                        Location = $"file:///thumbnails/{value}.png"
                    },
                    ct);
                return value;
            },
            cancellationToken: cancellationToken);
        return image;
    }
}

internal sealed class AppendActivity : IActivity<string, string>
{
    private readonly string suffix;

    public AppendActivity(string suffix) => this.suffix = suffix;

    public Task<string> ExecuteAsync(
        string input,
        CancellationToken cancellationToken) => Task.FromResult(input + suffix);
}
