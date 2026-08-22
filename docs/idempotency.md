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
| `RestartStepAsync` | Creates another operation | Each call bumps the fencing generation and inserts a fresh revision. Repeating restarts again. To make a restart retry-safe, check the run status before re-issuing. |
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

## What is not guaranteed

- `SendSignalAsync` is not deduplicated: two calls with the same payload are two
  signals. Use a `SignalDefinition<TPayload>` and rely on the run's wait step to
  consume exactly one.
- Bulk operations (`CancelManyAsync`) are applied independently and may
  partially succeed; the result reports per-item failures.
