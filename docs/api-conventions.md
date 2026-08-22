# API conventions

Consistent vocabulary makes the surface predictable. Zhinu uses this conceptual
distinction:

```text
GetX       retrieve a bounded, materialized result
QueryX     apply explicit filtering (with pagination options)
EnumerateX stream results (IAsyncEnumerable), never materializes the whole set
FindX      (reserved) identity/lookup by a stable key
```

## Resource matrix

| Resource | Single | Materialized query | Stream |
| --- | --- | --- | --- |
| Runs | `GetRunAsync` | `GetRunsAsync(RunQuery)` | `EnumerateRunsAsync(RunQuery)` |
| Events | — | `GetEventsAsync(afterSequence, limit)` | `EnumerateEventsAsync` |
| Artifacts | `GetLatestArtifactAsync` / `GetArtifactAsync(id)` | `QueryArtifactsAsync(ArtifactQuery)` | `EnumerateArtifactsAsync` |
| Signals | — | `GetSignalsAsync(SignalQuery)` | — |

`GetRunsAsync(RunQuery)` is the one name that leans on `Get` for a filtered
query; it predates the convention and is retained to avoid a churn-only break.
Every new query API follows `Query*` for filtered, `Enumerate*` for streaming.

## Handle surface rule

`WorkflowHandle<T>` answers only "what do I naturally do with this particular
run?" It may expose a method when the operation needs only the handle's run
identity **and** is a common run-scoped action:

```text
Wait, GetResult, GetRun, GetRunProgress, Cancel, SendSignal, Diagnose,
GetEvents, GetArtifacts, QueryArtifacts, GetLatestArtifact, GetSignals,
PurgeSignals, UpdateRunMetadata, Subscribe
```

It deliberately does **not** expose bulk operations, fork/rollback management,
global querying, administrative purge, store maintenance, or enumeration of huge
histories. Those belong on the runtime/query/admin surface.

## Nullability and empty results

- `Task<WorkflowRun?>` means "run may not exist"; returning `null` is the not-found
  signal, never an empty sentinel.
- Read collections return empty collections, never `null`.
- `T` in workflow/signal generics is non-nullable unless the payload type permits
  it; `T?` marks a genuinely nullable payload.
- Cursor `AfterId` values that reference a missing row throw
  `WorkflowNotFoundException` so callers cannot silently continue from a stale
  cursor.

## Exceptions

All Zhinu exceptions derive from `ZhinuException`. Catch `WorkflowStateException`
to cover fencing/lease and not-found errors; `WorkflowPersistenceException`
covers store failures; `WorkflowSerializationException` covers contract
mismatches. Raw provider exceptions (for example SQLite) do not escape public
APIs.
