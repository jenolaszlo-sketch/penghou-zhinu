# Observability

Zhinu emits standard .NET `ActivitySource` and `Meter` diagnostics. The core
runtime does not reference the OpenTelemetry SDK, and workflow correctness never
depends on a listener or exporter.

Use durable workflow events to determine what committed. Use traces to explain
how an execution segment unfolded, metrics for aggregate health, and logs for
detailed messages.

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

Each run stores a W3C trace ID. A resumed execution creates a new span segment
with the same trace ID. If execution resumes under a different ambient trace,
the workflow span links to it. The stored trace ID is diagnostic only and is
never used for claims, recovery, or state transitions.

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
