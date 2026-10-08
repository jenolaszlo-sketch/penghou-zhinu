# Observability

Zhinu emits standard .NET `ActivitySource` and `Meter` diagnostics. The core
runtime does not reference the OpenTelemetry SDK, and workflow correctness never
depends on a listener or exporter.

Use durable workflow events to determine what committed. Use traces to explain
how an execution segment unfolded, metrics for aggregate health, and logs for
detailed messages.

State rows remain authoritative; the event stream is a committed-transition
journal, not a replacement execution model. Every `WorkflowEvent` carries a
`Durability`: `Durable` marks a committed execution transition that state rows
reflect, and `Advisory` marks non-authoritative progress or diagnostics (for
example `progress`). The classification is derived from the persisted event type
via `WorkflowEventTypes.Durability`, so it is stable across reopen and does not
change event ordering or export/cursor semantics; application-defined event
types default to durable.

## Sources

```text
ActivitySource: Penghou.Zhinu
Meter:          Penghou.Zhinu
ActivitySource: Penghou.Zhinu.Sqlite
Meter:          Penghou.Zhinu.Sqlite
```

`ZhinuDiagnostics` and `ZhinuSqliteDiagnostics` expose stable source, activity,
attribute, and metric names.

## OpenTelemetry

The optional `Penghou.Zhinu.OpenTelemetry` package registers both tracing and
metrics without choosing an exporter:

```csharp
services.AddOpenTelemetry()
    .AddZhinuInstrumentation()
    .UseOtlpExporter();
```

Applications may instead call `AddSource` and `AddMeter` directly.

## Durable correlation

Each run stores a W3C trace ID. A child run started through `StartChildAsync`
inherits the parent run's durable trace ID, so a parent and its child segments
join one trace. A resumed execution creates a new span segment with the same
trace ID. If execution resumes under a different ambient trace, the workflow
span links to it. The stored trace ID is diagnostic only and is never used for
claims, recovery, or state transitions.

Continuity is durable and process-independent: the trace ID is persisted with
the run and read back after the store is reopened, without any in-process
`Activity` context. Trace correlation is not execution identity — a child keeps
its own run and step identities, and unrelated runs carry distinct trace IDs.
`ChildTraceContinuityTests` proves this across a real process boundary.

## Privacy and cardinality

Zhinu never records workflow inputs, outputs, prompts, signal payloads, SQL,
file contents, arbitrary metadata, or credentials in built-in diagnostics.
Durable identifiers appear on trace spans for correlation but never as metric
labels. Metric dimensions are limited to bounded workflow and outcome values.

Metric cardinality is a deliberate design constraint:

**Safe dimensions (bounded):**

```text
workflow.name
workflow.version
operation
status
```

**Usually unsafe (avoid as metric labels):**

```text
run_id                      belongs in traces and logs, not metric labels
step key when dynamically generated (for example fan-out prefixes)
signal payload / data
metadata
artifact name when user-generated
exception message
```

Artifact publication emits `zhinu.artifact.publish` with run, producer-step,
artifact name/type, revision, and creation-disposition attributes. Locations,
hashes, and custom artifact metadata are deliberately excluded. Newly created
references increment `zhinu.artifacts.published`; idempotent re-publication does
not increment it.

Class-based step spans include `zhinu.step.implementation.key` and
`zhinu.step.implementation.type` when an implementation is actually resolved.
Replayed completed steps do not resolve an implementation and therefore do not
emit resolution metadata.

Application events emitted through `WorkflowStepContext.EmitAsync` are durable
workflow data, not built-in telemetry. Their event type and payload are selected
by the application and must follow its own privacy and cardinality policy.

Protected execution also persists bounded authorization context/result evidence
and separate approval checkpoints in SQLite. These are application audit data,
not ordinary `WorkflowEvent` entries or built-in OpenTelemetry payloads.
`GetEventsAsync` alone is not a complete authorization audit. Use the optional
repository's pending-checkpoint API for current approval state and a trusted,
store-aware audit reader for retained outcomes. Keep credentials and policy
tokens out of declarations, provider results and correlation values. A recorded
Allowed result is history, never a transferable permission.

Operationally useful counters and histograms the runtime maintains:

```text
zhinu.runs.started / completed / failed / cancelled / active
zhinu.run.duration            (histogram, seconds)
zhinu.steps.claimed / executed / reused / failed / retried
zhinu.step.duration           (histogram, seconds)
zhinu.claim.latency           (histogram, seconds)
zhinu.signals.buffered / delivered
zhinu.compensations.executed / failed
zhinu.rollbacks.completed
zhinu.leases.expired / recovered
zhinu.fencing.rejections
zhinu.artifacts.published
```

SQLite metrics cover connection latency, failures, and busy/locked errors.
Detailed SQLite connection and initialization spans require
`ZhinuSqliteOptions.EnableDetailedDiagnostics`; SQL text remains excluded.
