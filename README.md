# Penghou.Zhinu

[![CI](https://github.com/jenolaszlo-sketch/penghou-zhinu/actions/workflows/ci.yml/badge.svg)](https://github.com/jenolaszlo-sketch/penghou-zhinu/actions/workflows/ci.yml)
[![NuGet Core](https://img.shields.io/nuget/vpre/Penghou.Zhinu?label=NuGet%20Core)](https://www.nuget.org/packages/Penghou.Zhinu)
[![NuGet SQLite](https://img.shields.io/nuget/vpre/Penghou.Zhinu.Sqlite?label=NuGet%20SQLite)](https://www.nuget.org/packages/Penghou.Zhinu.Sqlite)
[![NuGet Hosting](https://img.shields.io/nuget/vpre/Penghou.Zhinu.Hosting?label=NuGet%20Hosting)](https://www.nuget.org/packages/Penghou.Zhinu.Hosting)
[![License](https://img.shields.io/github/license/jenolaszlo-sketch/penghou-zhinu)](LICENSE)

Penghou.Zhinu is a lightweight, embedded durable workflow engine for .NET. It
persists workflow and step state in SQLite so applications can recover from
crashes or restarts without running a separate workflow server, message broker,
PostgreSQL, Redis, or Docker infrastructure.

Zhinu is designed for durable, replay-safe orchestration of AI workflows,
coding agents, developer tools, local automation, batch processing, and other
workloads where expensive or side-effecting operations should not be repeated
after their results are committed.

## Features

- Replay-safe durable steps with transactional SQLite persistence, leases,
  fencing, crash recovery, retries, delays, deadlines, and cancellation.
- Parallel steps, explicit dependencies, buffered external signals, and
  deterministic child workflows.
- Typed run handles, status snapshots, progress trees, event inspection,
  metadata queries, and retention controls.
- Previewable workflow forks that create a new run while reusing compatible
  committed steps and preserving explicit source lineage.
- Durable artifact references with content identity, custom metadata, exact
  producing-step provenance, revision history, and post-failure inspection.
- Compensations plus resumable rollback and rollback-and-restart operations,
  including dry-run plans before durable state changes are applied.
- Optional hosting, Microsoft Agent Framework checkpointing, isolated workflow
  test hosts, and custom-store conformance tests.
- OpenTelemetry traces and metrics with durable cross-process correlation,
  bounded dimensions, and privacy-safe defaults.

## Framework support

All packages target **.NET 8.0** and **.NET 10.0**.

| Package | Purpose | Target frameworks |
| --- | --- | --- |
| `Penghou.Zhinu` | Host-independent workflow engine, contracts, retries, durable events, and inspection | net8.0, net10.0 |
| `Penghou.Zhinu.Sqlite` | Transactional SQLite state, leases, recovery, and schema management | net8.0, net10.0 |
| `Penghou.Zhinu.Hosting` | Optional `Microsoft.Extensions.Hosting` execution loop and DI registration | net8.0, net10.0 |
| `Penghou.Zhinu.Agents` | Optional Microsoft Agent Framework integration and durable SQLite checkpointing | net8.0, net10.0 |
| `Penghou.Zhinu.Testing` | Isolated test host and custom-store conformance checks | net8.0, net10.0 |
| `Penghou.Zhinu.OpenTelemetry` | Optional tracing and metrics registration helpers | net8.0, net10.0 |

## Install

Install the SQLite store, then add the optional packages you need:

```bash
dotnet add package Penghou.Zhinu.Sqlite --prerelease
dotnet add package Penghou.Zhinu.Hosting --prerelease   # optional
dotnet add package Penghou.Zhinu.Agents --prerelease    # optional
dotnet add package Penghou.Zhinu.Testing --prerelease   # test projects
dotnet add package Penghou.Zhinu.OpenTelemetry --prerelease # optional
```

## Typed handles and result inspection

Use a typed handle when application code needs to retain, cancel, subscribe to,
or inspect a run without repeatedly passing its identifier:

```csharp
WorkflowHandle<CodeResult> handle =
    await engine.StartHandleAsync<CodeRequest, CodeResult>(
        "code-generation", "1", request);

WorkflowResult<CodeResult> snapshot = await handle.GetResultAsync();
if (!snapshot.IsTerminal)
    Console.WriteLine($"Run {handle.WorkflowRunId} is {snapshot.Status}");

CodeResult result = await handle.WaitAsync();
```

`GetResultAsync` does not throw for failed, cancelled, compensated, or pending
runs. `WaitAsync` retains exception-based application-flow semantics.

## Artifact outputs and chaining

Keep large files, repositories, model packages, and other blobs in storage
owned by your application. Zhinu durably records small immutable references to
them, including their logical name, type/version, location, content hash,
metadata, and exact producing step revision:

```csharp
var package = await workflow.StepAsync(
    "package",
    async (step, cancellationToken) =>
    {
        var path = await BuildPackageAsync(cancellationToken);
        return await step.PublishArtifactAsync(
            new WorkflowArtifactDescriptor
            {
                Name = "nuget-package",
                ArtifactType = "application/zip",
                ArtifactVersion = request.Version,
                Location = new Uri(path).AbsoluteUri,
                ContentHash = await Sha256Async(path, cancellationToken),
                Metadata = new Dictionary<string, string>
                {
                    ["packageId"] = "Penghou.Example"
                }
            },
            cancellationToken);
    });
```

Publication is durable immediately; the reference remains available even if
the producing step or workflow fails afterward. Identical publication by the
same step revision is idempotent. A conflicting publication is rejected, while
restarting the step produces the next artifact revision.

```csharp
IReadOnlyList<WorkflowArtifactReference> artifacts =
    await engine.GetArtifactsAsync(runId);

WorkflowArtifactReference? artifact =
    await engine.GetArtifactAsync(artifactId);

WorkflowArtifactReference? latest =
    await engine.GetLatestArtifactAsync(runId, "nuget-package");

var latestPackages = await engine.QueryArtifactsAsync(
    runId,
    new ArtifactQuery
    {
        ArtifactType = "application/zip",
        ProducerStepKey = "package",
        AfterId = lastArtifactId,
        Limit = 50
    });
```

`ArtifactQuery` is an immutable record. Cursor pagination (`AfterId`, ordered by
`created_at, id`) is stable under concurrent publication; `Offset` is available
for simple jumps but can skip or duplicate rows under concurrency. The latest
revision of a named artifact is served by `GetLatestArtifactAsync`.

Every new publication atomically appends an `artifact-published` durable event
and emits the `zhinu.artifact.publish` activity plus the
`zhinu.artifacts.published` counter. Applications can register one or more
`IWorkflowArtifactValidator` instances through
`ZhinuOptions.ArtifactValidators` to enforce location schemes, required hashes,
naming rules, or metadata policies before persistence.

Artifact references are ordinary serializable values, so they can be returned
from steps, passed as downstream step inputs, and included in a typed workflow
output. Outputs that want a discoverable convention may implement
`IArtifactProducingOutput`; Zhinu does not require a particular output shape.
When a fork reuses a producing step, its serialized output retains the original
immutable reference and source-run provenance, so downstream work in the new
run can continue the same artifact chain without republishing it.
Run-level publication is also available through
`WorkflowContext.PublishArtifactAsync`, but step publication is preferred when
the artifact has a producing step because it captures stronger provenance.

## OpenTelemetry

Zhinu emits activities and metrics from `Penghou.Zhinu`; SQLite emits bounded
provider diagnostics from `Penghou.Zhinu.Sqlite`. Configure the sources
directly or install `Penghou.Zhinu.OpenTelemetry` and call:

```csharp
services.AddOpenTelemetry().AddZhinuInstrumentation();
```

Exporters remain application-owned. Durable events remain authoritative for
committed state. Each workflow run persists a W3C trace ID, allowing execution
resumed by another process to remain correlated. Inputs, outputs, prompts,
signal payloads, SQL, and exception messages are excluded from built-in
telemetry.

Detailed SQLite connection and initialization spans are opt-in:

```csharp
services.AddZhinuSqlite(options =>
{
    options.DatabasePath = "zhinu.db";
    options.EnableDetailedDiagnostics = true;
});
```

See [`docs/observability.md`](docs/observability.md) for source names, spans,
metrics, correlation, privacy, and cardinality conventions.

## Testing workflows and stores

`Penghou.Zhinu.Testing` supplies `ZhinuTestHost`, an isolated temporary SQLite
engine, plus `WorkflowStoreConformance` for validating custom stores. Dispose
the host after each test to cancel local work and remove its database. This
keeps workflow integration tests independent without requiring an external
database or service.

## Preview API policy

The API is still evolving and preview releases may contain deliberate breaking
changes. Store implementations must honor the atomicity statements on
repository methods. `dotnet pack` package validation and the full multi-target
test suite run in CI for every change.

Direct construction requires only the core engine and a store implementation.
`Penghou.Zhinu.Hosting` adds the hosted execution loop and DI registration.
`Penghou.Zhinu.Agents` adds Microsoft Agent Framework integration.

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

When not using the hosting package, call `RunAvailableAsync` during application
startup. It recovers expired leases and executes currently runnable workflows.

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
key per iteration. A durable polling pattern is to keep the polling loop inside a single step
and use an ordinary `Task.Delay` between status checks. Split out only work that
must not replay as its own durable step:

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
handle, which is safe because status reads are idempotent. Call `EmitAsync` to
record durable, caller-visible progress that survives reconnection.

## Progress and inspection

Engine state transitions and their diagnostic events commit transactionally.
Current state remains authoritative; events are not used for execution replay.

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

**Atomicity:** `EmitAsync` called **inside a step delegate** is buffered and
committed atomically with the step's result and `step-completed` event: if the
step fails, its emitted events are rolled back too, and after a crash the step
and its events survive or are lost together. `EmitAsync` called **outside a
step** (between steps, at workflow top level) still appends in its own
transaction, so such events are durable but best-effort diagnostic output that
can diverge from the surrounding run.

If an `IWorkflowEventPublisher` is registered, committed events are also
forwarded after the store write (best-effort). The store remains the
authoritative source: subscribers should reconcile by re-reading events after
their last sequence.

Inputs and outputs are not copied into progress events by default.

A single call returns a point-in-time operational snapshot of a run and its
entire child-run subtree: the run, durable steps, artifacts, active maintenance
operation, diagnosis, recent events, fork source, and the same shape recursively
for every child started with `StartChildAsync`:

```csharp
var progress = await engine.GetRunProgressAsync(runId);
progress.Run.Status;               // WorkflowStatus.Completed
progress.CompletedSteps;           // 3
progress.ExecutedStepKeys;         // ["parent-step", "child:start", "child:wait"]
progress.Artifacts;                // durable external-artifact references
progress.Diagnosis?.Summary;       // deterministic current-state explanation
progress.SourceRun;                // source run when this is a fork
progress.SourceLineage;            // nearest fork source first
foreach (var child in progress.Children)
    Console.WriteLine($"{child.Run.WorkflowName}: {child.Run.Status}");
```

The subtree is fetched with a recursive CTE over `parent_run_id` and capped at
`RunProgressOptions.MaxDepth` (default 8). Events, artifacts, diagnosis, active
operation, and source-run lookup can be disabled individually. Returns null for
an unknown run.

For a lightweight operational explanation without the complete snapshot:

```csharp
RunDiagnosis? diagnosis = await engine.DiagnoseAsync(runId);
Console.WriteLine($"{diagnosis?.Code}: {diagnosis?.Summary}");
```

Stable diagnosis codes distinguish terminal runs, ready work, active leases,
retry/delay/signal waits, dependency blocking, expired leases, permanent step
failures, active operations, missing registrations, expired deadlines, and runs
awaiting a worker.

## SQLite schema compatibility

Every new database records `ZhinuSqliteSchema.CurrentVersion`. Initialization
checks that value before using an existing database and throws
`ZhinuSchemaCompatibilityException` with both expected and detected versions
when they differ. Unversioned preview databases are also rejected with an
explicit instruction to recreate them. Preview releases currently do not run
schema migrations.

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

Attach caller metadata (correlation IDs, owners, tags) when starting, and
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

Cancel many runs with a query. Bulk operations are applied independently and
may partially succeed; the result reports per-item failures:

```csharp
BulkOperationResult cancelled = await engine.CancelManyAsync(
    new RunQuery { Statuses = new[] { WorkflowStatus.Pending } },
    actor: "ops",
    reason: "maintenance");
Console.WriteLine($"Cancelled {cancelled.Succeeded}, failed {cancelled.FailedCount}");
```

Prefer purging terminal runs; deleting an active run abandons its execution
record.

## Parallel durable steps

Run one durable step per item in parallel with `FanOutAsync`. Each item is
independently durable. After a restart, completed items are reused and only
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

- `StepRestartMode.Dependents` (default): invalidates the target step and its
  **transitive durable dependents**, reusing unrelated branches.
- `StepRestartMode.StepOnly`: invalidates just the target, explicitly opting
  out of dependent invalidation.
- `StepRestartMode.CreationOrder`: the legacy behavior, invalidating the target plus every
  step created at or after it, useful for graphs built before dependencies
  existed or for a coarse invalidation.

Each restart is one atomic transaction: the run's **fencing generation** is
bumped, the run resets, and every invalidated step gets a fresh **execution
revision** (a new `Pending` row; the previous revision is preserved, never
deleted, so history and audit remain intact). If the run is currently executing
in this process, that execution is cancelled first (best-effort). Restarting a
run that another process is executing is not supported; cancel it there first.

Because step rows carry the generation in effect when they were claimed, any
worker that held a lease before the restart is **fenced out**: its subsequent
claims throw `LeaseLostException` and its lease renewals or step completions
are rejected, so a stale worker can never commit output to a restarted run.
Run metadata and deadline are preserved; clear the deadline yourself if a
restart should extend it.

## Forking into a new run

Use a fork when work should continue under a new workflow identity rather than
rewinding the source run. A fork preserves the source workflow name, version,
serialized input, and output contract. It atomically creates a pending run,
copies completed results outside the selected invalidation boundary, and
records the source in `WorkflowRun.SourceRunId`:

```csharp
var preview = await engine.PlanForkAsync(sourceRunId, "build");
// preview.StepsToReuse
// preview.StepsToReexecute, including requested/dependent/incomplete reasons

var forkId = await engine.ForkAsync(
    sourceRunId,
    "build",
    new ForkRunOptions
    {
        Mode = StepRestartMode.Dependents,
        Actor = "demo-ui",
        Reason = "try another build implementation"
    },
    cancellationToken);
```

The hosted worker will discover the pending fork, or an application can call
`ExecuteAsync(forkId)`. The source remains unchanged and may be running,
failed, or completed. Only completed steps are reusable; pending, running,
waiting, and failed steps execute again. Source deadlines are deliberately not
inherited.

Fork safety follows the same dependency rules as selective restart. Declare
`DependsOn` edges for precise invalidation, or explicitly choose
`StepRestartMode.CreationOrder` for a coarse prefix/suffix boundary. Zhinu
validates durable workflow and input compatibility, but applications remain
responsible for validating external workspace, file, or service state before
reusing a result that refers to it.

## Compensations

Steps that mutate the outside world can register a **compensation**, a
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

The compensation receives the **committed forward result**, often the exact
resource ID needed to undo the operation:

```text
Create VM -> VM ID = vm-8127 -> compensation receives vm-8127 -> Delete VM
```

Compensation metadata is persisted **separately** from step revisions, in its
own table (`workflow_step_compensations`), so forward history and rollback
history stay independently understandable. Each compensation row carries its
own lifecycle: status, attempts, retry policy, execution timeout, idempotency
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

- **Pending**: registered when the compensated step is claimed; the committed
  result is filled in when the forward step completes.
- **Skipped**: the forward step failed terminally, so there is no committed
  result to undo (a scheduled retry re-arms the registration).
- **Running / Completed / Failed**: reserved for compensation execution.

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

`PlanRollbackAsync` resolves, without changing any state, what a rollback
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
it with a chain `plan -> payment -> deploy -> tests` plus an independent
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
lease generation, and safe to retry: a rollback that fails midway (for
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

Typed signals bind the signal name to its payload type at compile time:

```csharp
var approval = new SignalDefinition<string>("approve");
await engine.SendSignalAsync(runId, approval, "ok");
var decision = await context.WaitForSignalAsync("approval", approval,
    timeout: TimeSpan.FromHours(48), cancellationToken);
```

The signal inbox is visible and bounded independently of the durable history.
Consumed signals remain in the workflow event history; purging the inbox does
not lose audit events:

```csharp
var inbox = await engine.GetSignalsAsync(runId,
    new SignalQuery { Status = SignalStatus.Buffered });
var purged = await engine.PurgeSignalsAsync(runId,
    new SignalPurgeOptions { OlderThan = DateTimeOffset.UtcNow.AddDays(-7) });
```

Semantics to be aware of:

- Signals are **buffered in the store**. A signal sent before any wait for it
  exists is held until a matching `WaitForSignalAsync` appears; a signal is
  then consumed by exactly one waiting step (oldest wait first, guarded
  update), so it cannot be double-delivered.
- `SignalSent` and `SignalDelivered` events are appended atomically with the
  signal, so a delivered signal is always observable.
- When the wait times out, the run fails with a `TimeoutException` while the
  wait step stays recorded as waiting; a late signal can still be consumed if
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
- The child's ID is **deterministic**: a hash of the parent ID and the step
  key. Replaying the parent therefore reuses the exact same child run instead of
  creating duplicates, even across a crash between creating the child and
  recording the step.
- Children execute **inline** by default: the parent's `ExecuteAsync` drives
  the child to completion while the parent holds its own lease, so a child run
  cannot be double-executed concurrently (the child's own claim arbitration
  decides). Depth is capped by `ZhinuOptions.MaxNestingDepth` (default 16) at
  child creation; deeper children are rejected rather than created unbounded.
- Child semantics are explicit via `ChildRunOptions`: the effective child
  deadline is the earlier of the parent's deadline and `ChildRunOptions.Deadline`
  (a child cannot outlive its parent), and metadata is inherited only when
  `InheritMetadata` is set (default off) or provided explicitly.
- Restarting the parent's `child:start` step creates a **fresh child** (a new
  deterministic invocation identity derived from the parent, step key, and step
  revision); a normal replay reuses the existing child.
- A child that fails or is cancelled propagates to the parent: failure throws
  `WorkflowExecutionFailedException`, cancellation throws
  `OperationCanceledException`.

## Microsoft Agent Framework (MAF)

`Penghou.Zhinu.Agents` turns MAF graph workflows into durable Zhinu steps and
gives MAF checkpointing a thread-safe, restart-surviving SQLite home.

Durable checkpoint store: MAF's built-in file store is single-process and not
thread-safe; this implementation lives in the same SQLite database as your runs:

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

MAF graph as a durable step: run a whole MAF workflow inside one Zhinu step.
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

A second sample exercises direct construction (no hosted loop): typed handles,
child workflows, typed signals, artifact publication, query, and durable
cancellation:

```bash
dotnet run --project samples/Penghou.Zhinu.Direct
```

## License

MIT
