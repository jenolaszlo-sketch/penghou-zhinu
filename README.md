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
dotnet add package Penghou.Zhinu.Agents --prerelease
```

Hosting is optional. Direct construction only requires the core and chosen
store implementation. Agents (Microsoft Agent Framework integration) is
optional.

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
- Expired leases are swept automatically. Recovery runs once at engine
  initialization and then at most every `ZhinuOptions.LeaseRecoveryInterval`
  (default 30 seconds), so a background scan loop does not issue a recovery
  write on every poll tick.

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
messaging service. `SubscribeAsync` is notified in-process when events are
committed by this engine, so subscribers wake immediately instead of polling
the store on every `PollInterval`; the poll interval remains a fallback for
events appended by other processes or before the subscription started.

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

A single call returns a point-in-time progress snapshot of a run and its whole
child-run subtree: the run, its durable steps, its recent events, and the same
shape recursively for every child started with `StartChildAsync`:

```csharp
var progress = await engine.GetRunProgressAsync(runId);
progress.Run.Status;               // WorkflowStatus.Completed
progress.CompletedSteps;           // 3
progress.ExecutedStepKeys;         // ["parent-step", "child:start", "child:wait"]
foreach (var child in progress.Children)
    Console.WriteLine($"{child.Run.WorkflowName}: {child.Run.Status}");
```

The subtree is fetched with a recursive CTE over `parent_run_id` and capped at
`RunProgressOptions.MaxDepth` (default 8); events per run can be disabled or
capped via `IncludeEvents` / `EventsLimit`. Returns null for an unknown run.

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

## Dependencies and restarting a step

Zhinu persists an explicit **dependency graph** for each run. Every step claim
transactionally records its edges (`workflow_step_dependencies`), so restarting
a step can invalidate exactly the steps that transitively depend on it instead
of approximating by creation order.

Dependencies are declared either per step or per scope:

```csharp
// Per step: this step depends on "upload" (and whatever the enclosing
// DependsOn scope declares).
await workflow.StepAsync("encode.video", input, operation,
    new StepOptions { DependsOn = ["upload"] }, ct);

// Per scope: every step created inside the scope depends on "prepare".
using (workflow.DependsOn("prepare"))
{
    await workflow.StepAsync("encode.video", input, operation, ct);
    await workflow.StepAsync("publish", output, operation, ct);
}
```

Nested scopes combine their dependencies. `FanOutAsync` items are independent
siblings (they never depend on each other), and child workflows get an automatic
edge from `"{step}:wait"` to `"{step}:start"`.

Inspect the recorded graph or preview a restart before applying it:

```csharp
var edges = await engine.GetDependencyGraphAsync(runId);
// edges like { StepKey = "encode.video", DependsOnStepKey = "upload" }

var plan = await engine.PlanRestartAsync(runId, "encode.video");
// plan.StepsToInvalidate: target first, then dependents with reasons

await engine.RestartStepAsync(runId, "encode.video",
    new RestartStepOptions
    {
        Mode = StepRestartMode.Dependents,
        Actor = "ops",
        Reason = "bad transcode"
    },
    cancellationToken);
```

`RestartStepAsync` resets the run to `Pending` and re-executes the invalidated
steps on the next execution. The prefix is reconstructed from committed results
(its delegates are not re-run). Restart modes:

- `StepRestartMode.Dependents` (default) — invalidates the target step and its
  **transitive durable dependents**, reusing unrelated branches.
- `StepRestartMode.StepOnly` — invalidates just the target, explicitly opting
  out of dependent invalidation.
- `StepRestartMode.CreationOrder` — the legacy behavior: the target plus every
  step created at or after it, useful for graphs built before dependencies
  existed or for a coarse invalidation.

Each restart is one atomic transaction: the run's **fencing generation** is
bumped, the run resets, and every invalidated step gets a fresh **execution
revision** (a new `Pending` row; the previous revision is preserved, never
deleted, so history and audit remain intact). If the run is currently executing
in this process, that execution is cancelled first (best-effort). Restarting a
run that another process is executing is not supported — cancel it there first.

Because step rows carry the generation in effect when they were claimed, any
worker that held a lease before the restart is **fenced out**: its subsequent
claims throw `LeaseLostException` and its lease renewals or step completions
are rejected, so a stale worker can never commit output to a restarted run.
Run metadata and deadline are preserved; clear the deadline yourself if a
restart should extend it.

## Compensations

Steps that mutate the outside world can register a **compensation** — a
delegate that undoes the committed forward result. Registration is a
first-class parameter of `StepAsync`, kept out of `StepOptions` (which is
execution policy) and carried internally as part of the step definition:

```csharp
var reservation = await context.StepAsync(
    "reserve",
    request,
    ReserveAsync,
    compensation: async (result, step, ct) =>
        await ReleaseAsync(result, step.IdempotencyKey, ct));
```

The compensation receives the **committed forward result** — often the exact
resource id needed to undo the operation:

```text
Create VM ──► VM id = vm-8127 ──► compensation receives vm-8127 ──► Delete VM
```

Compensation metadata is persisted **separately** from step revisions, in its
own table (`workflow_step_compensations`), so forward history and rollback
history stay independently understandable. Each compensation row carries its
own lifecycle — status, attempts, retry policy, execution timeout, idempotency
key, started/completed timestamps, failure, fencing generation, and the
actor/reason of the rollback it belongs to:

```csharp
var compensations = await engine.GetCompensationsAsync(runId);
foreach (var c in compensations)
{
    Console.WriteLine($"{c.StepKey} rev {c.Revision}: {c.Status}");
    // c.InputJson is the committed forward result the compensation would undo.
}
```

Lifecycle:

- **Pending** — registered when the compensated step is claimed; the committed
  result is filled in when the forward step completes.
- **Skipped** — the forward step failed terminally, so there is no committed
  result to undo (a scheduled retry re-arms the registration).
- **Running / Completed / Failed** — reserved for compensation execution.

Registration is durable at claim time and never duplicated on replay; a
restarted step revision registers its own compensation row, so
`GetCompensationsAsync` shows the compensation history across revisions.

## Rollbacks

Once a run completes (or fails), the **rollback** API undoes its committed
work by executing registered compensations. Rollback never re-runs forward
operations: the workflow definition is replayed against the stored committed
results to re-bind compensation delegates, and only those delegates execute.

Idempotency keys are stable across retries, restarts, and rollback attempts, so
downstream systems can deduplicate every side effect:

```text
forward step        <run>:<step>:<revision>
compensation        <run>:<step>:<revision>:compensation
```

Both keys are unchanged when an attempt is retried and change only when a
restart creates a new revision. Compensation execution is **at-least-once**:
a compensation that already completed is never run again, and a failed
compensation is retried (with its own `RetryPolicy`) on the next rollback
attempt.

### Planning a rollback

`PlanRollbackAsync` resolves — without changing any state — what a rollback
would do to each step and why:

```csharp
// Full rollback: every step with a committed result and a claimable
// compensation, in reverse dependency order.
var plan = await engine.PlanRollbackAsync(runId);

// Roll back to a specific step.
var toPlan = await engine.PlanRollbackAsync(
    runId,
    "deploy",
    new RollbackOptions(RollbackBoundary.BeforeStep));

foreach (var step in plan.Steps)
    Console.WriteLine($"{step.StepKey}: {step.Action} ({step.Reason})");
```

`RollbackBoundary.BeforeStep` includes the target step itself;
`RollbackBoundary.AfterStep` preserves it. A plan entry states what would
happen (`Compensate` or `Preserve`) and why (`Boundary`, `Dependent`,
`Ancestor`, or `IndependentBranch`). For example, rolling back `deploy` before
it with a chain `plan → payment → deploy → tests` plus an independent
`frontend` yields:

```text
tests      Compensate  Dependent
deploy     Compensate  Boundary
plan       Preserve    Ancestor
payment    Preserve    Ancestor
frontend   Preserve    IndependentBranch
```

### Executing a rollback

```csharp
// Compensate every completed, compensatable forward step, in reverse
// dependency order, then mark the run Compensated.
await engine.RollbackAsync(runId);

// Roll back to a step. AfterStep leaves the target's committed work intact
// and compensates only its dependents; BeforeStep compensates the target too.
await engine.RollbackToStepAsync(runId, "deploy", RollbackBoundary.AfterStep);
await engine.RollbackToStepAsync(runId, "payment", RollbackBoundary.BeforeStep);
```

A successful rollback moves the run to the terminal `Compensated` state; the
run's status itself records that its forward history was undone. Rollback of a
`Completed` or `Failed` run is serialized by a lease, fenced by the run's
lease generation, and safe to retry: a rollback that fails mid-way (for
example a permanently failing compensation) leaves the run `Failed` and
claimable again, and already-completed compensations are reused rather than
re-executed.

## External signals

Workflows can durably wait for external input. `WaitForSignalAsync` is a
replay-safe boundary: the wait is recorded in the run's history, so a workflow
that is waiting survives restarts and keeps waiting:

```csharp
var approval = await context.WaitForSignalAsync<string>(
    "wait.approval",      // durable step key
    "approve",            // signal name
    timeout: TimeSpan.FromHours(48),
    cancellationToken);
```

The signal is delivered from outside the workflow:

```csharp
await engine.SendSignalAsync(runId, "approve", new { decision = "ok" });
```

Semantics to be aware of:

- Signals are **buffered in the store**. A signal sent before any wait for it
  exists is held until a matching `WaitForSignalAsync` appears; a signal is
  then consumed by exactly one waiting step (oldest wait first, guarded
  update), so it cannot be double-delivered.
- `SignalSent` and `SignalDelivered` events are appended atomically with the
  signal, so a delivered signal is always observable.
- When the wait times out, the run fails with a `TimeoutException` while the
  wait step stays recorded as waiting — a late signal can still be consumed if
  the step is later restarted.
- Sending a signal to a finished run buffers it but it is never delivered;
  the `SignalSent` event still records that it was sent.
- The wait's payload type must match what the sender serialized; data is
  deserialized with the run's serializer settings.

## Child workflows

A workflow can start another workflow and durably wait for its result:

```csharp
var thumbnail = await context.StartChildAsync<VideoRef, Thumbnail>(
    "thumbnails",          // step key prefix; child id derives from this
    "video.thumbnail",     // child workflow name
    "1",                   // child workflow version
    input,
    cancellationToken);
```

- The child is an ordinary run linked via `ParentRunId`, so it shows up in
  queries and is executed by the same machinery (leases, recovery, deadlines).
- The child's id is **deterministic** — a hash of the parent id and the step
  key — so replaying the parent reuses the exact same child run instead of
  creating duplicates, even across a crash between creating the child and
  recording the step.
- Children execute **inline** by default: the parent's `ExecuteAsync` drives
  the child to completion while the parent holds its own lease, so a child run
  cannot be double-executed concurrently (the child's own claim arbitration
  decides). Depth is capped by `ZhinuOptions.MaxNestingDepth` (default 16);
  deeper children are left for the poll loop / background host.
- A child that fails or is cancelled propagates to the parent: failure throws
  `WorkflowExecutionFailedException`, cancellation throws
  `OperationCanceledException`.

## Microsoft Agent Framework (MAF)

`Penghou.Zhinu.Agents` turns MAF graph workflows into durable Zhinu steps and
gives MAF checkpointing a thread-safe, restart-surviving SQLite home.

Durable checkpoint store — MAF's built-in file store is single-process and not
thread-safe; this one lives in the same SQLite database as your runs:

```csharp
var store = new SqliteJsonCheckpointStore(new ZhinuSqliteOptions
{
    DatabasePath = "zhinu.db"
});
// pass to any MAF run you want to survive a crash:
var checkpoints = CheckpointManager.CreateJson(store);
await InProcessExecution.RunStreamingAsync(
    workflow, input, checkpoints, sessionId);
```

MAF graph as a durable step — run a whole MAF workflow inside one Zhinu step.
The terminal output commits with the step; a replay reuses it without touching
MAF. If a previous attempt of the step crashed mid-run, execution resumes from
the most recent MAF checkpoint instead of starting over:

```csharp
var result = await context.RunAgentWorkflowAsync<string, string>(
    "agent-analysis",   // durable step key
    workflow,           // the MAF Workflow to run
    input,
    checkpointStore,    // SqliteJsonCheckpointStore or any ICheckpointStore<JsonElement>
    cancellationToken);
```

- MAF checkpoints are written per superstep under a session derived from the
  run and step; a durable store such as `SqliteJsonCheckpointStore` therefore
  lets a long graph continue from its last superstep after a crash or a
  `RestartStepAsync`.
- `RetrieveIndexAsync` must return the most recent checkpoint first; the
  SQLite store does, so the first entry is the resume point.
- The step fails the run when the MAF workflow terminates with an error
  (`AgentWorkflowExecutionException`). Plain executor-graph workflows run to
  completion autonomously; turn-based agent teams that require
  `TrySendMessageAsync` are outside the current helper.

DI registration (optional):

```csharp
services.AddZhinuSqliteCheckpoints(options => options.DatabasePath = "zhinu.db");
```

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
