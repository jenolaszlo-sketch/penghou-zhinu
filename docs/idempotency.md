# Idempotency semantics

Every externally callable mutation in Zhinu falls into one of four categories:
**idempotent**, **rejected on conflict**, **creates another logical operation**,
or **safe only with an idempotency key**. This page states the guarantee for
each operation so retries are safe to reason about.

A **retry** in Zhinu means: the caller saw an ambiguous outcome (network error,
process crash, timeout) and repeats the call. The table below states whether the
repeated call is safe.

| Operation | Category | Behavior on repeat |
| --- | --- | --- |
| `StartAsync` (no run id) | Creates another run | A fresh pending run is created each call. |
| `StartAsync` (with `workflowRunId`) | Idempotent / rejected | Same name+version+input → returns the existing run id. Different workflow or input for the same id → `WorkflowStateException`. |
| `CancelAsync` | Idempotent | Cancelling an already-cancelled run is a no-op; terminal runs are untouched. |
| `SendSignalAsync` | Creates another signal | Each call buffers a new signal. Delivery is exactly-once per signal, but sending is additive. |
| `SendSignalWithReceiptAsync` | Idempotent / rejected | Identical run, name, and canonical JSON payload under one `SignalId` return the original durable receipt. Conflicting reuse throws `WorkflowOperationConflictException`. |
| `RestartStepAsync` (no operation id) | Creates another operation | Each call bumps the fencing generation and inserts a fresh revision. Repeating restarts again. |
| `RestartStepAsync` / `RestartStepWithReceiptAsync` (with `RestartStepOptions.OperationId`) | Idempotent / rejected | Identical intent returns the original durable receipt and does not restart again. Reusing the operation ID with a different run, target, mode, actor, or reason throws `WorkflowOperationConflictException`. |
| `ForkAsync` | Creates another run | Each call creates a new pending run. Supply a fixed `ForkRunOptions.WorkflowRunId` to detect duplicates (conflicting reuse is rejected). |
| `RollbackAsync` / `RollbackToStepAsync` | Idempotent in effect | At-least-once: already-completed compensations are reused, never re-run. A rollback of an already-`Compensated` run is a no-op / rejected. |
| `RollbackAndRestartAsync` | Resumable, not idempotent | The operation is durable and resumes after a crash; issuing a new one while one is active is arbitrated by the lease/generation. |
| `PublishArtifactAsync` | Idempotent / rejected | An identical publication in the same run/step revision returns the existing reference (`Created = false`). A conflicting publication is rejected. |
| `StartChildAsync` | Idempotent on replay | The child id is deterministic (`parent + step key + revision`); replaying reuses the child. Restarting `child:start` creates a fresh child. |
| `CompleteStepAsync` | Effectively-once | A completed step returns its committed result without re-running its delegate. Completion itself is fenced by lease/generation. |
| `CompleteCompensationAsync` | At-least-once | A completed compensation is never executed again. |

## Stable downstream keys

For external side effects, Zhinu exposes a stable idempotency key to step
delegates and compensations so the downstream system can deduplicate:

```text
forward step        <run>:<step>:<revision>
compensation        <run>:<step>:<revision>:compensation
```

These keys are unchanged across retries and change only when a restart creates a
new revision.

## Administrative restart receipts

Use a caller-generated, stable operation ID whenever a restart command might be
retried after an ambiguous response:

```csharp
var receipt = await engine.RestartStepWithReceiptAsync(
    runId,
    "generate",
    new RestartStepOptions
    {
        OperationId = approvalId,
        Mode = StepRestartMode.Dependents,
        Actor = userId,
        Reason = "Approved revised implementation"
    },
    cancellationToken);
```

The SQLite provider commits the generation bump, pending step revisions,
`step-restarted` event, and operation receipt in one transaction. The receipt
contains the durable event sequence and applied generation. `WasApplied` is true
for the committing call and false when an identical retry reads the existing
receipt. Consumers can therefore mirror the event to another audit store with
at-least-once delivery and use the operation ID or event sequence for
deduplication.

Providers implement this contract through
`IIdempotentWorkflowRestartRepository`. The reusable store conformance suite
checks concurrent retry, conflict detection, generation stability, and single
event publication.

## Idempotent signal receipts

Use a stable signal identity for user responses or other commands that may be
retried after an ambiguous network result:

```csharp
var receipt = await engine.SendSignalWithReceiptAsync(
    runId,
    "approval",
    new SignalSendOptions { SignalId = responseId },
    response,
    cancellationToken);
```

SQLite commits the signal inbox row, `signal-sent` event, and operation receipt
in one transaction. `WasBuffered` is true only for the call that committed the
signal. An identical retry returns the original event sequence with
`WasBuffered == false`, including after the signal was consumed and its inbox
row was purged. The durable operation receipt therefore remains the authority
for whether the send committed.

Conflict identity includes the workflow run, ordinal signal name, and canonical
JSON payload. Object-property order and insignificant whitespace do not change
the payload identity; array order remains significant. Reuse for different
intent throws `WorkflowOperationConflictException`. Providers advertise this
optional capability through `IIdempotentWorkflowSignalRepository`, and hosted
applications can depend on `IIdempotentWorkflowClient`.

## What is not guaranteed

- The original `SendSignalAsync` deliberately remains additive: two calls with
  the same payload are two signals. Use `SendSignalWithReceiptAsync` when the
  caller is retrying one logical command.
- Bulk operations (`CancelManyAsync`) are applied independently and may
  partially succeed; the result reports per-item failures.
