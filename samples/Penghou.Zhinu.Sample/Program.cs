using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Penghou.Zhinu;
using Penghou.Zhinu.Hosting;
using Penghou.Zhinu.Sqlite;

var builder = Host.CreateApplicationBuilder(args);
var dataDirectory = Path.Combine(AppContext.BaseDirectory, "data");
Directory.CreateDirectory(dataDirectory);
var runIdPath = Path.Combine(dataDirectory, "active-run.txt");
builder.Services.AddZhinuSqlite(options =>
    options.DatabasePath = Path.Combine(dataDirectory, "zhinu.db"));
builder.Services.AddZhinu(options =>
{
    options.PollInterval = TimeSpan.FromMilliseconds(100);
});
builder.Services.AddZhinuWorkflow<SampleWorkflow, string, string>(
    "restartable-sample",
    "1");

using var host = builder.Build();
await host.StartAsync();
var engine = host.Services.GetRequiredService<WorkflowEngine>();
Guid runId;
if (File.Exists(runIdPath) &&
    Guid.TryParse(await File.ReadAllTextAsync(runIdPath), out var storedRunId) &&
    await engine.GetRunAsync(storedRunId) is { Status: not WorkflowStatus.Completed })
{
    runId = storedRunId;
    Console.WriteLine($"Resuming workflow {runId:D}.");
}
else
{
    runId = await engine.StartAsync(
        "restartable-sample",
        "1",
        "sample");
    await File.WriteAllTextAsync(runIdPath, runId.ToString("D"));
    Console.WriteLine($"Started workflow {runId:D}.");
}

Console.WriteLine("Terminate this process during a step, then run it again.");
var handle = engine.GetHandle<string>(runId);
await foreach (var progress in handle.SubscribeAsync())
{
    Console.WriteLine(
        $"[{progress.Timestamp:HH:mm:ss}] {progress.EventType}" +
        (progress.StepKey is null ? string.Empty : $": {progress.StepKey}"));
}
var result = await handle.WaitAsync();
Console.WriteLine($"Result: {result}");
File.Delete(runIdPath);
await host.StopAsync();

internal sealed class SampleWorkflow : IWorkflow<string, string>
{
    public async Task<string> RunAsync(
        WorkflowContext context,
        string input,
        CancellationToken cancellationToken)
    {
        var analysis = await context.StepAsync(
            "analyze",
            async ct => await WorkAsync("Analyze", input, ct),
            cancellationToken: cancellationToken);
        var plan = await context.StepAsync(
            "plan",
            async ct => await WorkAsync("Plan", analysis, ct),
            cancellationToken: cancellationToken);
        var implementation = await context.StepAsync(
            "implement",
            async ct => await WorkAsync("Implement", plan, ct),
            cancellationToken: cancellationToken);
        await context.DelayAsync(
            "cool-down",
            TimeSpan.FromSeconds(1),
            cancellationToken);
        return implementation;
    }

    private static async Task<string> WorkAsync(
        string name,
        string input,
        CancellationToken cancellationToken)
    {
        Console.WriteLine($"Executing {name}; terminate now to test recovery.");
        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        return $"{input}-{name.ToLowerInvariant()}";
    }
}
