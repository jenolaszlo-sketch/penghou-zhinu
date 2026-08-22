# Penghou.Zhinu

[![CI](https://github.com/jenolaszlo-sketch/penghou-zhinu/actions/workflows/ci.yml/badge.svg)](https://github.com/jenolaszlo-sketch/penghou-zhinu/actions/workflows/ci.yml)
[![NuGet Core](https://img.shields.io/nuget/vpre/Penghou.Zhinu?label=NuGet%20Core)](https://www.nuget.org/packages/Penghou.Zhinu)
[![NuGet SQLite](https://img.shields.io/nuget/vpre/Penghou.Zhinu.Sqlite?label=NuGet%20SQLite)](https://www.nuget.org/packages/Penghou.Zhinu.Sqlite)
[![NuGet Hosting](https://img.shields.io/nuget/vpre/Penghou.Zhinu.Hosting?label=NuGet%20Hosting)](https://www.nuget.org/packages/Penghou.Zhinu.Hosting)
[![License](https://img.shields.io/github/license/jenolaszlo-sketch/penghou-zhinu)](LICENSE)

Penghou.Zhinu is a lightweight, embedded durable workflow engine for .NET. It
persists workflow and step state in SQLite, allowing applications to recover
from crashes and restarts without operating a separate workflow server,
message broker, PostgreSQL cluster, Redis instance, or Docker infrastructure.

Zhinu is useful when work is expensive, long-running, or side-effecting and
must not restart from zero after a process exits. Typical uses include AI and
coding workflows, local automation, batch processing, media generation, and
multi-stage application jobs.

```text
ordinary async workflow code
        -> durable step boundaries
        -> transactional SQLite state
        -> crash recovery and inspection
```

## Why Zhinu

- **Embedded:** runs inside your .NET application.
- **Durable:** committed step results survive process loss.
- **Replay-safe:** completed steps are reused instead of executed again.
- **Operational:** includes leases, fencing, retries, cancellation, signals,
  progress, diagnosis, artifacts, and OpenTelemetry.
- **Honest about side effects:** interrupted delegates are at-least-once;
  stable idempotency keys support downstream deduplication.
- **Host-independent:** use direct construction or the optional hosted worker.

Zhinu stores current durable state rather than replaying an event history. When
a process restarts, the workflow method runs again from its entry point.
Completed `StepAsync` calls return their committed results, reconstructing
execution until the first unfinished boundary.

## Packages

All packages target .NET 8 and .NET 10.

| Package | Purpose |
| --- | --- |
| `Penghou.Zhinu` | Core workflow engine and contracts |
| `Penghou.Zhinu.Sqlite` | Transactional SQLite store, leases, and recovery |
| `Penghou.Zhinu.Hosting` | `Microsoft.Extensions.Hosting` execution loop and DI |
| `Penghou.Zhinu.Hosting.AspNetCore` | Liveness, readiness, and diagnostics endpoints |
| `Penghou.Zhinu.OpenTelemetry` | Trace and metric registration helpers |
| `Penghou.Zhinu.Testing` | Isolated workflow test host and store conformance suite |
| `Penghou.Zhinu.Agents` | Optional Microsoft Agent Framework checkpoint integration |

For the common hosted setup:

```bash
dotnet add package Penghou.Zhinu.Sqlite --prerelease
dotnet add package Penghou.Zhinu.Hosting --prerelease
```

## Five-minute quick start

Register SQLite, the hosted engine, and a workflow:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Penghou.Zhinu;
using Penghou.Zhinu.Hosting;
using Penghou.Zhinu.Sqlite;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddZhinuSqlite(options =>
    options.DatabasePath = "zhinu.db");

builder.Services.AddZhinu(options =>
    options.MaxConcurrentWorkflows = 4);

builder.Services.AddZhinuWorkflow<OrderWorkflow, OrderRequest, OrderResult>(
    "process-order",
    "1");

using var host = builder.Build();
await host.StartAsync();

var engine = host.Services.GetRequiredService<WorkflowEngine>();
var handle = await engine.StartHandleAsync<OrderRequest, OrderResult>(
    "process-order",
    "1",
    new OrderRequest("order-42"));

OrderResult result = await handle.WaitAsync();
Console.WriteLine(result.Confirmation);
```

Define the workflow as ordinary async code with explicit durable steps:

```csharp
public sealed class OrderWorkflow : IWorkflow<OrderRequest, OrderResult>
{
    public async Task<OrderResult> RunAsync(
        WorkflowContext workflow,
        OrderRequest request,
        CancellationToken cancellationToken)
    {
        var validated = await workflow.StepAsync(
            "validate",
            request,
            (input, ct) => ValidateAsync(input, ct),
            cancellationToken: cancellationToken);

        return await workflow.StepAsync(
            "submit",
            validated,
            (input, step, ct) => SubmitAsync(
                input,
                idempotencyKey: step.IdempotencyKey,
                ct),
            new StepOptions
            {
                Retry = new RetryPolicy
                {
                    MaxAttempts = 3,
                    InitialDelay = TimeSpan.FromSeconds(2)
                },
                ExecutionTimeout = TimeSpan.FromMinutes(2)
            },
            cancellationToken);
    }

    private static Task<ValidatedOrder> ValidateAsync(
        OrderRequest request,
        CancellationToken cancellationToken) =>
        Task.FromResult(new ValidatedOrder(request.OrderId));

    private static Task<OrderResult> SubmitAsync(
        ValidatedOrder order,
        string idempotencyKey,
        CancellationToken cancellationToken) =>
        Task.FromResult(new OrderResult(
            $"confirmed:{order.OrderId}:{idempotencyKey}"));
}

public sealed record OrderRequest(string OrderId);
public sealed record ValidatedOrder(string OrderId);
public sealed record OrderResult(string Confirmation);
```

`workflowRunId` is an optional idempotency key for starting the run. Repeating
the same workflow, version, input, and ID returns the existing run. Reusing the
ID with a different contract or input fails explicitly.

## Delivery guarantee

Zhinu provides:

- **effectively-once reuse** of durably completed step results;
- **at-least-once execution** of a step interrupted before its result commits;
- transactional state transitions and diagnostic events;
- lease fencing so stale workers cannot commit after ownership changes.

Zhinu cannot guarantee exactly-once external side effects. A process can exit
after a remote operation succeeds but before its step result is committed. Put
external effects inside durable steps and pass `WorkflowStepContext.IdempotencyKey`
to downstream systems whenever possible.

Code outside durable steps may run again after recovery. Control flow should be
derived from workflow input and previously committed step results.

See [execution semantics](docs/semantics.md) and
[idempotency guidance](docs/idempotency.md) for the precise contract.

## Inspecting a run

A typed handle contains the common operations for one durable run:

```csharp
WorkflowResult<OrderResult> snapshot = await handle.GetResultAsync();

if (!snapshot.IsTerminal)
{
    WorkflowRunProgress? progress = await handle.GetRunProgressAsync();
    Console.WriteLine($"{progress?.CompletedSteps} steps completed");
}

RunDiagnosis? diagnosis = await handle.DiagnoseAsync();
IReadOnlyList<WorkflowEvent> events = await handle.GetEventsAsync();
```

`GetResultAsync` returns a non-throwing snapshot for pending, completed, failed,
cancelled, and compensated runs. `WaitAsync` uses exception-based application
flow and returns only a successful typed result.

Subscribe to durable events and reconnect from the last observed sequence:

```csharp
await foreach (var progressEvent in handle.SubscribeAsync(afterSequence: 42))
    Console.WriteLine($"{progressEvent.Sequence}: {progressEvent.EventType}");
```

Current state is authoritative. Events support diagnostics, audit, and progress;
they are not used to replay workflow execution.

## Recovery and hosting

`Penghou.Zhinu.Hosting` continuously scans for runnable work, renews leases, and
recovers expired ownership. Without the hosting package, construct
`WorkflowEngine` directly and call `RunAvailableAsync` during startup.

The optional ASP.NET Core package exposes operational endpoints:

```csharp
app.MapZhinuEndpoints("/zhinu");
```

- `GET /zhinu/liveness` checks the process without touching storage.
- `GET /zhinu/readiness` verifies that the store and schema are usable.
- `GET /zhinu/diagnostics` reports bounded runtime and SQLite health data.

These endpoints do not expose workflow inputs, outputs, signal bodies, artifact
contents, or other payload data.

## Observability

Zhinu emits privacy-safe activities and metrics. Durable events remain the
authoritative record of committed state. Inputs, outputs, prompts, signal
payloads, SQL, and exception messages are excluded from built-in telemetry.

```csharp
services.AddOpenTelemetry()
    .AddZhinuInstrumentation();
```

See [observability](docs/observability.md) for source names, metrics,
correlation, privacy, and cardinality conventions.

## Testing

`Penghou.Zhinu.Testing` provides:

- `ZhinuTestHost` for isolated workflow integration tests;
- `WorkflowStoreConformanceSuite` for custom durable stores;
- reusable checks for concurrency, fencing, recovery, signals, artifacts,
  child workflows, and transaction behavior.

Store implementations must preserve the atomicity and fencing rules described
in the [store contract](docs/store-contract.md).

## Advanced capabilities

The code-first runtime also supports:

- parallel steps and explicit dependency graphs;
- durable delays and external signals;
- deterministic child workflows;
- selective step restart and previewable forks;
- compensation, rollback, and rollback-and-restart;
- durable external-artifact references with producing-step provenance;
- run metadata, querying, pagination, retention, and bulk operations;
- schema compatibility checks and failure diagnosis.

The runnable [hosted sample](samples/Penghou.Zhinu.Sample/Program.cs) demonstrates
process recovery. The [direct-construction sample](samples/Penghou.Zhinu.Direct/Program.cs)
demonstrates typed handles, signals, child workflows, artifacts, and
cancellation without dependency injection.

Detailed contracts:

- [Execution semantics](docs/semantics.md)
- [API conventions](docs/api-conventions.md)
- [Store contract](docs/store-contract.md)
- [Observability](docs/observability.md)
- [Idempotency](docs/idempotency.md)
- [Trimming and Native AOT](docs/trimming.md)
- [Public API policy](docs/public-api-policy.md)
- [Roadmap](ROADMAP.md)

## When not to use Zhinu

Zhinu is probably not the right choice when you need:

- distributed workers, multi-region availability, or a managed workflow
  control plane;
- a remote task queue or general-purpose message broker;
- exactly-once external side effects without downstream idempotency;
- cron scheduling as the primary product capability;
- durable storage for large files or binary artifacts;
- a model provider, autonomous agent framework, or application-specific user
  interface.

For those cases, use infrastructure designed for that responsibility and, when
useful, compose it with Zhinu at explicit durable step boundaries.

## Project direction

Zhinu is independently useful as a code-first durable workflow engine. Its
roadmap adds validated declarative workflow definitions, activity catalogues,
capability enforcement, revision-bound evidence, and bounded AI activities.
Natural-language methodology compilation belongs above the runtime and will be
pursued only after hand-authored declarative workflows are proven.

The API is currently preview and may evolve between preview releases. Public
surface changes are tracked through shipped/unshipped API baselines and package
validation.

## License

[MIT](LICENSE)
