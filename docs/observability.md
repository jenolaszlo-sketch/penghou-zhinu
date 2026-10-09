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

## Event page reads

`IWorkflowEventPageReader.GetEventPageAsync(workflowRunId, afterSequence, limit)`
(and the store-level `IWorkflowEventPageRepository.ReadEventPageAsync`) returns a
`WorkflowEventPage` over the same per-run `WorkflowEvent.Sequence` ordering as
`GetEventsAsync`. It is a **stateless** read: the caller holds the cursor. It is
independent of durable export acknowledgement
(`IWorkflowEventExportRepository`); the two share ordering but not state.

- **Cursor:** the last sequence already consumed. A read after cursor `N`
  returns events with `Sequence > N` — the boundary is strictly exclusive, so a
  page never repeats the event at `N`.
- **`nextCursor`:** the final returned event's sequence, or the supplied cursor
  when the page is empty. It is monotonic (never decreases); pass it back as the
  next `afterSequence`.
- **`hasMore`:** whether more events exist beyond this page. Reported explicitly
  by reading one row past `limit`; callers must not infer it from
  `Events.Count == limit`.
- **Empty page:** when no events exist after the cursor (including a run that
  does not exist), `Events` is empty, `nextCursor` equals the supplied cursor,
  and `hasMore` is false.
- **`throughDurableSequence`:** the highest sequence at or before `nextCursor`
  that is a `Durable` event. It never advances on advisory events and never
  regresses across pages (an advisory-only tail keeps the prior durable
  position). It is distinct from `nextCursor`.
- **Retention floor:** the earliest cursor from which incremental continuation
  remains valid. Zhinu does not truncate events within a run (purge removes whole
  runs), so this is `0` today.
- **`resyncRequired`:** whether the supplied cursor can no longer be safely
  continued because required history is unavailable. With no intra-run
  truncation this is always `false` today, and it is never set for empty pages,
  no-new-events, or advisory-only tails.
- **Page read vs export acknowledgement:** a page read is stateless and changes
  no durable state; export
  (`ReadExportBatchAsync`/`AcknowledgeExportAsync`) carries a durable per-consumer
  cursor that also gates retention. The page API neither reads nor advances
  export state.

This is **not** the snapshot-at-watermark handshake and does not imply snapshot
atomicity. `throughDurableSequence` is the highest durable event sequence known
from the returned pages, not a projection watermark; see Run snapshot below.

## Run snapshot

`IWorkflowSnapshotReader.GetRunSnapshotAsync(workflowRunId, options)` (store:
`IWorkflowSnapshotRepository.ReadRunSnapshotAsync`) returns a
`WorkflowRunSnapshot`: the run, current-revision steps, dependency edges,
waits, artifacts, external operations (bounded), the active operation,
generation + instance + dispositions, source run and lineage, recursive child
snapshots, a derived diagnosis, and `ThroughDurableSequence`.

The snapshot deliberately excludes historical step revisions (only
current-revision rows are returned), journal events themselves (use event pages),
export acknowledgement state, and anything outside durable storage. Direct store
reads leave `Diagnosis` unset; the engine derives it from the same boundary's
rows.

**Watermark meaning.** `ThroughDurableSequence` (D) means: all authoritative
state represented in this snapshot includes every durable execution transition
through sequence D, and no state change caused solely by a durable transition
after D is represented. Advisory events with sequences beyond D may exist and do
not invalidate the snapshot.

**Atomicity.** Snapshot rows and the watermark come from one consistent read
boundary: a single deferred read transaction over one connection, rolled back
without writing. Every entity query runs inside that boundary. A concurrent
writer either fully precedes or fully follows the snapshot; no row can reflect
a durable transition past D. The boundary works in both WAL and rollback-journal
modes; connection busy-timeout applies at transaction start, matching ordinary
reads.

**Advisory events.** Ordering includes advisory entries where they occur, but
advisories never advance the watermark: D is the highest *durable* sequence in
the boundary view. A snapshot may legitimately report D = 102 while advisory
events 103–104 already exist; event-page reads from 102 still expose them.

**Intended consumer protocol.**

```text
1. Read authoritative snapshot => watermark D
2. Initialize local projection from snapshot
3. Read event pages after D
4. Apply events in sequence order
5. Continue from NextCursor
```

Three distinct concepts, not to be conflated: the snapshot watermark is
durable-state consistency of one read boundary; `NextCursor` is event-stream
traversal position; `ThroughDurableSequence` on an event page is the durable
progress within returned pages.

**Export cursors.** Snapshot reads never allocate an export consumer and neither
read nor advance export acknowledgement state; durable export remains a separate
integration mechanism.

**Restart.** No in-memory watermark state is kept: reopening the store (new
instance over the same file, or a new process) and reading again yields the same
watermark for the same committed history.

**Nonexistent run.** A snapshot read for a run that does not exist returns null,
consistent with the existing run-reading APIs.

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
