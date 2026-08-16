# Penghou.Zhinu

Penghou.Zhinu is a lightweight, embedded durable workflow engine for .NET. It
persists workflow and step state in SQLite so applications can resume after
crashes or restarts without operating a workflow server, message broker,
PostgreSQL, Redis, or Docker infrastructure.

Zhinu is designed for AI workflows, coding agents, developer tools, local
automation, batch processing, and other applications containing expensive
operations that should not be repeated after their results are committed.

## Packages

| Package | Purpose |
| --- | --- |
| `Penghou.Zhinu` | Host-independent workflow engine, contracts, retries, durable events, and inspection |
| `Penghou.Zhinu.Sqlite` | Transactional SQLite state, leases, recovery, and schema management |
| `Penghou.Zhinu.Hosting` | Optional `Microsoft.Extensions.Hosting` execution loop and DI registration |

## Install

```bash
dotnet add package Penghou.Zhinu.Sqlite --prerelease
dotnet add package Penghou.Zhinu.Hosting --prerelease
```

Hosting is optional. Direct construction only requires the core and chosen
store implementation.

## Hosted quick start

```csharp
services.AddZhinuSqlite(options =>
    options.DatabasePath = "zhinu.db");

services.AddZhinu(options =>
{
    options.MaxConcurrentWorkflows = 4;
});

services.AddZhinuWorkflow<CodeWorkflow, CodeRequest, CodeResult>(
    "code-generation",
    "1");
```

Define ordinary async workflow code:

```csharp
public sealed class CodeWorkflow
    : IWorkflow<CodeRequest, CodeResult>
{
    public async Task<CodeResult> RunAsync(
        WorkflowContext workflow,
        CodeRequest request,
        CancellationToken cancellationToken)
    {
        var plan = await workflow.StepAsync(
            "plan",
            request,
            (input, ct) => CreatePlanAsync(input, ct),
            cancellationToken: cancellationToken);

        return await workflow.StepAsync(
            "implement",
            plan,
            (input, ct) => ImplementAsync(input, ct),
            new StepOptions
            {
                Retry = new RetryPolicy
                {
                    MaxAttempts = 3,
                    InitialDelay = TimeSpan.FromSeconds(2)
                },
                ExecutionTimeout = TimeSpan.FromMinutes(10)
            },
            cancellationToken);
    }
}
```

Start without blocking the caller:

```csharp
var runId = await engine.StartAsync(
    "code-generation",
    "1",
    request,
    workflowRunId: callerSuppliedId,
    cancellationToken);
```

The caller-supplied ID makes repeated identical start requests idempotent. A
conflicting workflow or input using the same ID is rejected.

## Direct construction

```csharp
var store = new SqliteWorkflowStore(new ZhinuSqliteOptions
{
    DatabasePath = "zhinu.db"
});
var registry = new WorkflowRegistry()
    .Register("code-generation", "1", new CodeWorkflow());
var engine = new WorkflowEngine(store, registry);

var result = await engine.RunAsync<CodeRequest, CodeResult>(
    "code-generation",
    "1",
    request);
```

Call `RunAvailableAsync` on application startup when not using the hosting
package. It recovers expired leases and executes currently runnable workflows.

## Recovery model

Zhinu does not replay an event history. It stores current run and step state
directly. Because .NET cannot restore a suspended async continuation after a
process exits, the registered workflow method is invoked again from its entry
point. Completed `StepAsync` calls deserialize their committed results without
invoking their delegates, reconstructing values until execution reaches the
unfinished boundary.

Consequences:

- code outside durable steps may execute again after restart;
- control flow must derive from workflow input and completed step results;
- external side effects belong inside durable, idempotent steps;
- changing the input or result contract of an existing step key fails clearly;
- workflow name and version select the exact registered implementation.

## Delivery guarantee

Zhinu provides **at-least-once execution for interrupted steps** and
**effectively-once reuse of durably completed results**. It cannot guarantee
exactly-once external side effects. A process can exit after a remote side
effect succeeds but before Zhinu commits completion.

Use the stable downstream key exposed to step delegates:

```csharp
await workflow.StepAsync(
    "submit-order",
    async (step, ct) => await api.SubmitAsync(
        request,
        idempotencyKey: step.IdempotencyKey,
        ct));
```

## Durable retries, delays, and cancellation

- Retry attempt and next eligibility time are persisted.
- Execution timeout is configurable per step.
- Workflow and step leases renew during long operations.
- Expired leases make interrupted work recoverable.
- `DelayAsync` persists its absolute deadline.
- `CancelAsync` persists cancellation and signals local running operations.

## Progress and inspection

State transitions and their diagnostic events commit transactionally. Current
state remains authoritative; events are not used for execution replay.

```csharp
var run = await engine.GetRunAsync(runId);
var steps = await engine.GetStepsAsync(runId);
var events = await engine.GetEventsAsync(runId, afterSequence: 42);

await foreach (var progress in engine.SubscribeAsync(runId))
    Console.WriteLine(progress.EventType);
```

Inputs and outputs are not copied into progress events by default. Applications
can reconnect and retrieve committed events after their last sequence without
requiring Redis or another messaging service.

## What Zhinu is not

- Not a distributed workflow cluster
- Not a remote task queue
- Not an event-sourced replay engine
- Not a cron scheduler
- Not an agent framework
- Not an exactly-once side-effect system
- Not a replacement for Temporal when distributed workers, high availability,
  multi-region operation, sophisticated signals, or mature operational tooling
  are required

Multi-process worker coordination is not a v0.1 guarantee. Store large files
and binary artifacts externally and persist paths or identifiers as step
results.

## Sample

```bash
dotnet run --project samples/Penghou.Zhinu.Sample
```

Terminate it during a step and run it again. Committed steps will be reused and
execution will continue from the interrupted boundary.

## License

MIT
