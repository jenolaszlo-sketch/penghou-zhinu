# Penghou.Zhinu

[![CI](https://github.com/jenolaszlo-sketch/penghou-zhinu/actions/workflows/ci.yml/badge.svg)](https://github.com/jenolaszlo-sketch/penghou-zhinu/actions/workflows/ci.yml)
[![NuGet Penghou.Zhinu](https://img.shields.io/nuget/vpre/Penghou.Zhinu)](https://www.nuget.org/packages/Penghou.Zhinu)
[![NuGet Penghou.Zhinu.Sqlite](https://img.shields.io/nuget/vpre/Penghou.Zhinu.Sqlite)](https://www.nuget.org/packages/Penghou.Zhinu.Sqlite)
[![NuGet Penghou.Zhinu.Hosting](https://img.shields.io/nuget/vpre/Penghou.Zhinu.Hosting)](https://www.nuget.org/packages/Penghou.Zhinu.Hosting)
[![License](https://img.shields.io/github/license/jenolaszlo-sketch/penghou-zhinu)](LICENSE)

Penghou.Zhinu is a lightweight, embedded durable workflow engine for .NET. It
persists workflow and step state in SQLite so applications can resume after
crashes or restarts without operating a workflow server, message broker,
PostgreSQL, Redis, or Docker infrastructure.

Zhinu is designed for AI workflows, coding agents, developer tools, local
automation, batch processing, and other applications containing expensive
operations that should not be repeated after their results are committed.

## Framework support

All packages target **.NET 8.0** and **.NET 10.0**.

| Package | Purpose | Target frameworks |
| --- | --- | --- |
| `Penghou.Zhinu` | Host-independent workflow engine, contracts, retries, durable events, and inspection | net8.0, net10.0 |
| `Penghou.Zhinu.Sqlite` | Transactional SQLite state, leases, recovery, and schema management | net8.0, net10.0 |
| `Penghou.Zhinu.Hosting` | Optional `Microsoft.Extensions.Hosting` execution loop and DI registration | net8.0, net10.0 |

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

## Serialization

Inputs and step results are persisted as JSON. `JsonSerializerOptions` flows
through the `WorkflowEngine` constructor (and `AddZhinu(...)`), so callers can
install converters, custom enums, or polymorphism:

```csharp
var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
options.Converters.Add(new MyConverter());

var engine = new WorkflowEngine(
    store,
    registry,
    serializerOptions: options);
```

Abstract or interface inputs and outputs require `System.Text.Json`
polymorphism configuration. Annotate the base type with
`[JsonPolymorphic]`/`[JsonDerivedType]`, or register a converter, before
passing options to the engine; otherwise serialization falls back to the
declared (base) type and derived fields are lost:

```csharp
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(ImageRequest), "image")]
[JsonDerivedType(typeof(VideoRequest), "video")]
public abstract record GenerationRequest;
```

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
- `StartAsync`/`RunAsync` accept an optional run-level `deadline`. A run claimed
  after its deadline is failed with a timeout error instead of executing,
  which bounds how long a stuck or orphaned run can hold resources.

## Polling and long-running loops

`DelayAsync` is a one-shot step: once it completes, re-claiming the same key
returns the committed result immediately, so a loop must not reuse one delay
key per iteration. The durable pattern for a poll loop is a single step whose
delegate owns the whole loop with an ordinary `Task.Delay` inside. Only the
work that must never replay is split out as a durable step:

```csharp
var handle = await workflow.StepAsync(
    "submit",
    request,
    async (req, step, ct) => await SubmitAsync(
        req, idempotencyKey: step.IdempotencyKey, ct),
    new StepOptions
    {
        Retry = new RetryPolicy { MaxAttempts = 1 }
    },
    cancellationToken);

var output = await workflow.StepAsync(
    "poll",
    handle,
    async (handle, step, ct) =>
    {
        while (true)
        {
            var snapshot = await GetAsync(handle, ct);
            if (snapshot.IsTerminal)
                return snapshot;
            await workflow.EmitAsync("progress", snapshot.Progress, ct);
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }
    },
    new StepOptions { ExecutionTimeout = TimeSpan.FromMinutes(10) },
    cancellationToken);
```

`submit` is durable and never re-executes after its result is committed. `poll`
holds a renewed lease for the whole wait; if the process dies mid-poll, the
step is re-acquired on recovery and re-runs its loop from the same committed
handle — safe because status reads are idempotent. Call `EmitAsync` to record
durable, caller-visible progress that survives reconnection.

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

Workflows can emit their own durable, replay-safe events from any step:

```csharp
await workflow.EmitAsync(
    WorkflowEventTypes.Progress,
    new { Percent = 40, Stage = "encoding" },
    cancellationToken);
```

Emitted events carry serialized data and survive restarts, so consumers can
reconnect after their last sequence without requiring Redis or another
messaging service.

**Atomicity caveat:** `EmitAsync` appends the event with its own store write,
not in the same transaction as the step that emitted it. If the process exits
after the event is committed but before the surrounding step completes, the
event survives but the step may re-run; conversely, if the step commits but the
process exits before the append, that event is lost. Treat emitted events as
durable but best-effort diagnostic output, not as part of the execution
contract.

If an `IWorkflowEventPublisher` is registered, committed events are also
forwarded after the store write (best-effort). The store remains the
authoritative source: subscribers should reconcile by re-reading events after
their last sequence.

Inputs and outputs are not copied into progress events by default.

## Querying, metadata, and retention

Runs can be queried with filters and stable cursor pagination:

```csharp
var page = await engine.GetRunsAsync(new RunQuery
{
    Statuses = new[] { WorkflowStatus.Running, WorkflowStatus.Pending },
    WorkflowName = "code-generation",
    Limit = 50,
    AfterId = lastPage[^1].Id
});
```

Attach caller metadata (correlation ids, owners, tags) when starting, and
update it later. Metadata never participates in idempotency:

```csharp
var runId = await engine.StartAsync(
    "code-generation",
    "1",
    request,
    metadata: new { CorrelationId = "abc-123", Owner = "agent-7" });

await engine.UpdateRunMetadataAsync(runId, new { CorrelationId = "abc-123", Stage = "done" });
```

Purge old runs to bound database growth. Deleting a run cascades to its steps
and events:

```csharp
var deleted = await engine.PurgeRunsAsync(
    DateTimeOffset.UtcNow.AddDays(-7),
    new[] { WorkflowStatus.Completed, WorkflowStatus.Failed });
```

Prefer purging terminal runs; deleting an active run abandons its execution
record.

## Parallel durable steps

Run one durable step per item in parallel with `FanOutAsync`. Each item is
independently durable — after a restart, completed items are reused and only
unfinished items re-run:

```csharp
var videos = await workflow.FanOutAsync(
    "encode",
    requests,
    async (request, step, ct) =>
    {
        var id = await provider.EncodeAsync(
            request,
            idempotencyKey: step.IdempotencyKey,
            ct);
        return id;
    },
    new StepOptions { Retry = new RetryPolicy { MaxAttempts = 3 } },
    cancellationToken);
```

Step keys are derived as `"{prefix}.{index}"`, so results stay aligned with the
input order.

## Waiting with a deadline

`WaitForCompletionAsync` accepts an absolute caller-side deadline and throws
`TimeoutException` if the run has not terminated by then:

```csharp
var result = await engine.WaitForCompletionAsync<string>(
    runId,
    DateTimeOffset.UtcNow.AddMinutes(5),
    cancellationToken);
```

## Restarting a step

`RestartStepAsync` invalidates a step and everything downstream of it without
touching the steps that came before, then resets the run to `Pending`:

```csharp
await engine.RestartStepAsync(runId, "encode.video", cancellationToken);
```

On the next execution the prefix is reconstructed from committed results (its
delegates are not re-run), while the restarted step and every step created at
or after it are claimed fresh and re-executed, so their new outputs propagate
to the run result. Use it to redo a bad generation, correct a failed batch,
or re-apply a fixed step after a bugfix.

Semantics to be aware of:

- "Everything downstream" is approximated by **creation order**: because Zhinu
  does not store an explicit dependency graph, the target step and every step
  created at or after it are invalidated. For linear pipelines and fan-out
  chains this is exact; for concurrently created fan-out siblings it may
  re-execute steps that did not actually depend on the target (harmless extra
  work under Zhinu's idempotent-step assumption, but worth knowing).
- If the run is currently executing in this process, that execution is
  cancelled first (best-effort). Restarting a run that another process is
  executing is not supported — cancel it there first.
- Run metadata and deadline are preserved; clear the deadline yourself if a
  restart should extend it.

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
