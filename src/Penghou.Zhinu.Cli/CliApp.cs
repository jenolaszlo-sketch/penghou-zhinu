using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;

namespace Penghou.Zhinu.Cli;

/// <summary>Operator inspection over an existing database. Reads never mutate; only `runs signal` writes.</summary>
internal static class CliApp
{
    public static async Task<int> RunAsync(string[] args, TextWriter output)
    {
        var options = Parse(args, output);
        if (options is null)
            return 1;
        try
        {
            var store = new SqliteWorkflowStore(new ZhinuSqliteOptions
            {
                DatabasePath = options.DatabasePath!,
                Pooling = false
            });
            await using var engine = new WorkflowEngine(
                store,
                new WorkflowRegistry(),
                new ZhinuOptions());
            var writer = new CliOutput(options.Format == "json", options.IncludePayloads);
            return await DispatchAsync(options, engine, store, writer, output)
                .ConfigureAwait(false);
        }
        catch (WorkflowNotFoundException exception)
        {
            await output.WriteLineAsync($"not found: {exception.Message}").ConfigureAwait(false);
            return 2;
        }
        catch (Exception exception)
        {
            await output.WriteLineAsync($"error: {exception.Message}").ConfigureAwait(false);
            return 1;
        }
    }

    private static async Task<int> DispatchAsync(
        CliOptions options,
        WorkflowEngine engine,
        SqliteWorkflowStore store,
        CliOutput writer,
        TextWriter output)
    {
        var cancellationToken = CancellationToken.None;
        if (options.Positionals.Count == 0 ||
            !string.Equals(options.Positionals[0], "runs", StringComparison.Ordinal))
        {
            await output.WriteLineAsync(
                "usage: zhinu --db <path> [--format text|json] runs <list|show|events|why-waiting|restart-preview|fork-preview|retention-preview|signal> ...")
                .ConfigureAwait(false);
            return 1;
        }
        return await RunsAsync(options, engine, store, writer, output, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<int> RunsAsync(
        CliOptions options,
        WorkflowEngine engine,
        SqliteWorkflowStore store,
        CliOutput writer,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        var sub = options.Positionals.Count > 1 ? options.Positionals[1] : null;
        switch (sub)
        {
            case "list":
                {
                    var query = new RunQuery
                    {
                        Limit = options.Int("limit", 100),
                        AfterId = options.GuidValue("after")
                    };
                    var status = options.Value("status");
                    if (status is not null)
                    {
                        if (!Enum.TryParse<WorkflowStatus>(status, ignoreCase: true, out var parsed))
                        {
                            await output.WriteLineAsync($"error: unknown status '{status}'.").ConfigureAwait(false);
                            return 1;
                        }
                        query = query with { Statuses = [parsed] };
                    }
                    var runs = await engine.GetRunsAsync(query, cancellationToken).ConfigureAwait(false);
                    await output.WriteLineAsync(writer.Render(writer.RunRows(runs))).ConfigureAwait(false);
                    return 0;
                }
            case "show":
                {
                    if (!options.RequireId(2, output, out var id))
                        return 1;
                    var run = await engine.GetRunAsync(id, cancellationToken).ConfigureAwait(false) ??
                        throw new WorkflowNotFoundException($"Workflow '{id:D}' does not exist.");
                    var steps = await engine.GetStepsAsync(id, cancellationToken).ConfigureAwait(false);
                    var waits = await engine.GetWaitsAsync(id, cancellationToken).ConfigureAwait(false);
                    await output.WriteLineAsync(
                        writer.Render(writer.RunDetailModel(run, steps, waits))).ConfigureAwait(false);
                    return 0;
                }
            case "events":
                {
                    if (!options.RequireId(2, output, out var id))
                        return 1;
                    _ = await engine.GetRunAsync(id, cancellationToken).ConfigureAwait(false) ??
                        throw new WorkflowNotFoundException($"Workflow '{id:D}' does not exist.");
                    var events = await engine.GetEventsAsync(
                        id,
                        options.Long("after", 0),
                        options.Int("limit", 100),
                        cancellationToken).ConfigureAwait(false);
                    await output.WriteLineAsync(writer.Render(writer.EventModels(events))).ConfigureAwait(false);
                    return 0;
                }
            case "why-waiting":
                {
                    if (!options.RequireId(2, output, out var id))
                        return 1;
                    var waits = await engine.GetWaitsAsync(id, cancellationToken).ConfigureAwait(false);
                    if (waits.Count == 0)
                        await output.WriteLineAsync("no parked waits recorded.").ConfigureAwait(false);
                    else
                        await output.WriteLineAsync(writer.Render(writer.WaitModels(waits))).ConfigureAwait(false);
                    try
                    {
                        var blocked = await engine.WaitUntilBlockedAsync(id, CancellationToken.None)
                            .WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
                        await output.WriteLineAsync(
                            $"run status: {blocked.Run.Status}; blocking: {blocked.BlockingWaits.Count}").ConfigureAwait(false);
                    }
                    catch (TimeoutException)
                    {
                        var run = await engine.GetRunAsync(id, cancellationToken).ConfigureAwait(false);
                        await output.WriteLineAsync(
                            $"run status: {run?.Status}; still has runnable work.").ConfigureAwait(false);
                    }
                    return 0;
                }
            case "restart-preview":
                {
                    if (!options.RequireId(2, output, out var id) ||
                        options.Positionals.Count < 4)
                    {
                        await output.WriteLineAsync("usage: runs restart-preview <id> <step> [--mode Dependents|StepOnly].")
                            .ConfigureAwait(false);
                        return 1;
                    }
                    var plan = await engine.PlanRestartAsync(
                        id,
                        options.Positionals[3],
                        options.EnumValue("mode", StepRestartMode.Dependents),
                        cancellationToken).ConfigureAwait(false);
                    await output.WriteLineAsync(writer.Render(writer.PlanModels(plan))).ConfigureAwait(false);
                    return 0;
                }
            case "fork-preview":
                {
                    if (!options.RequireId(2, output, out var id) ||
                        options.Positionals.Count < 4)
                    {
                        await output.WriteLineAsync("usage: runs fork-preview <id> <step> [--mode Dependents|StepOnly].")
                            .ConfigureAwait(false);
                        return 1;
                    }
                    var plan = await engine.PlanForkAsync(
                        id,
                        options.Positionals[3],
                        options.EnumValue("mode", StepRestartMode.Dependents),
                        cancellationToken).ConfigureAwait(false);
                    await output.WriteLineAsync(writer.Render(writer.ForkPlanModels(plan))).ConfigureAwait(false);
                    return 0;
                }
            case "retention-preview":
                {
                    var days = options.Double("older-than-days", 30);
                    var preview = await store.PreviewRetentionAsync(
                        new RunRetentionOptions
                        {
                            OlderThan = DateTimeOffset.UtcNow.AddDays(-days),
                            IncludeActiveRuns = options.Flag("include-active")
                        },
                        cancellationToken).ConfigureAwait(false);
                    await output.WriteLineAsync(
                        writer.Render(writer.RetentionModel(preview))).ConfigureAwait(false);
                    return 0;
                }
            case "signal":
                {
                    if (!options.RequireId(2, output, out var id) ||
                        options.Positionals.Count < 4)
                    {
                        await output.WriteLineAsync("usage: runs signal <id> <name> [--data JSON].")
                            .ConfigureAwait(false);
                        return 1;
                    }
                    await engine.SendSignalAsync(
                        id, options.Positionals[3], options.Value("data"), cancellationToken)
                        .ConfigureAwait(false);
                    await output.WriteLineAsync(writer.Render(
                        new CliOutput.SignalResult(id.ToString("D"), options.Positionals[3])))
                        .ConfigureAwait(false);
                    return 0;
                }
            default:
                await output.WriteLineAsync(
                    "usage: runs <list|show|events|why-waiting|restart-preview|fork-preview|retention-preview|signal> ...")
                    .ConfigureAwait(false);
                return 1;
        }
    }

    private static CliOptions? Parse(string[] args, TextWriter output)
    {
        var options = new CliOptions();
        var positionals = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg == "--db" && i + 1 < args.Length)
                options.DatabasePath = args[++i];
            else if (arg == "--format" && i + 1 < args.Length)
                options.Format = args[++i];
            else if (arg == "--include-payloads")
                options.IncludePayloads = true;
            else if (arg == "--include-active")
                options.Flags["include-active"] = "true";
            else if (arg.StartsWith("--", StringComparison.Ordinal) && i + 1 < args.Length)
                options.Flags[arg[2..]] = args[++i];
            else if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                output.WriteLine($"error: flag '{arg}' needs a value.");
                return null;
            }
            else
            {
                positionals.Add(arg);
            }
        }
        options.Positionals = positionals;
        if (string.IsNullOrWhiteSpace(options.DatabasePath))
        {
            output.WriteLine("error: --db <path> is required.");
            return null;
        }
        if (options.Format is not ("text" or "json"))
        {
            output.WriteLine("error: --format must be text or json.");
            return null;
        }
        if (positionals.Count == 0)
        {
            output.WriteLine("error: expected a command (runs ...).");
            return null;
        }
        return options;
    }

    private sealed class CliOptions
    {
        public string? DatabasePath { get; set; }
        public string Format { get; set; } = "text";
        public bool IncludePayloads { get; set; }
        public List<string> Positionals { get; set; } = [];
        public Dictionary<string, string> Flags { get; } = new(StringComparer.OrdinalIgnoreCase);

        public string? Value(string name) =>
            Flags.TryGetValue(name, out var value) ? value : null;

        public bool Flag(string name) => Flags.ContainsKey(name);

        public int Int(string name, int fallback) =>
            Value(name) is { } raw && int.TryParse(raw, out var parsed) ? parsed : fallback;

        public long Long(string name, long fallback) =>
            Value(name) is { } raw && long.TryParse(raw, out var parsed) ? parsed : fallback;

        public double Double(string name, double fallback) =>
            Value(name) is { } raw && double.TryParse(raw, out var parsed) ? parsed : fallback;

        public Guid? GuidValue(string name) =>
            Value(name) is { } raw && System.Guid.TryParse(raw, out var parsed) ? parsed : null;

        public TEnum EnumValue<TEnum>(string name, TEnum fallback) where TEnum : struct =>
            Value(name) is { } raw && Enum.TryParse<TEnum>(raw, ignoreCase: true, out var parsed)
                ? parsed
                : fallback;

        public bool RequireId(int index, TextWriter output, out Guid id)
        {
            id = Guid.Empty;
            if (Positionals.Count <= index ||
                !System.Guid.TryParse(Positionals[index], out id))
            {
                output.WriteLine("error: expected a run id argument.");
                return false;
            }
            return true;
        }
    }
}
