# Execution semantics

This page states the precise meaning of Zhinu's durability guarantees. These are
the semantics that are easy to get subtly wrong, so they are written down here
rather than only implied by code.

## Exactly-once, effectively-once, at-least-once

- **Step execution is effectively-once for committed results.** Once a step's
  output is committed, re-executing the workflow reuses that output and never
  invokes the step delegate again. The delegate is at-least-once: a crash after
  a side effect but before the commit means the step may run again. Use the
  stable idempotency key (`<run>:<step>:<revision>`) for downstream dedup.
- **Compensation execution is at-least-once.** A completed compensation is never
  run again; a failed one is retried on the next rollback attempt.
- **Signal delivery is exactly-once.** A buffered signal is consumed by exactly
  one waiting step (oldest wait first, guarded update).
- **Artifact publication is idempotent** within a run/step revision: an identical
  publication returns the existing reference; a conflicting one is rejected.

## What survives a restart

- Completed step results, retry schedules (next eligible time), durable delays
  (absolute deadline), signal waits, child links, compensations, rollback
  operations, artifact references, run metadata, and the full event history.
- The workflow **method runs again from its entry point**. Code outside durable
  steps may re-execute. Completed `StepAsync` calls deserialize committed results
  without invoking delegates, reconstructing values until execution reaches the
  unfinished boundary.
- `MaxNestingDepth` bounds the child tree at creation; a restart of a parent's
  `child:start` step creates a fresh child (deterministic new invocation
  identity), while a normal replay reuses the existing child.

## Retry guarantee

- A failed step is retried only if `RetryPolicy.MaxAttempts > 1`. The attempt
  count, error, and next eligible time are persisted. Retry backoff is
  deterministic (no jitter). The step stays `Waiting` until its `AvailableAt`,
  then re-claims as a new attempt.
- Retries are durable: a process restart between attempts preserves the attempt
  count and schedule.

## Cancellation meaning

- `CancelAsync` persists a `Cancelled` run and best-effort signals any local
  execution holding that run. Running work is asked to stop cooperatively via
  the cancellation token; it is not killed.
- Cancelling propagates to the run's child subtree.
- A run claimed after cancellation is not executed. Cancellation is distinct
  from caller `CancellationToken`: cancelling a `WaitAsync`/`ExecuteAsync` call
  does not cancel the workflow; use `CancelAsync` for that.

## Workflow version disappears

- If a registered workflow name/version is removed, runs of it fail with
  `WorkflowDefinitionUnavailableException` on the next execution. Completed runs
  are unaffected; their results remain readable.
- Restarting or rolling back a run whose definition is gone also fails, because
  those operations replay the definition.

## Which operations are atomic

| Operation | Atomicity |
| --- | --- |
| Step claim + dependencies + compensation registration | one transaction |
| Step completion + result + compensation input + event | one transaction |
| Step failure + retry schedule + event | one transaction |
| Run completion / failure / cancellation + event | one transaction |
| Signal buffer + `signal-sent` event | one transaction |
| Signal delivery + step completion + `signal-delivered` event | one transaction |
| Artifact publish + `artifact-published` event | one transaction |
| Restart (generation bump + run reset + new revisions + event) | one transaction |
| Fork (new run + copied steps + source lineage) | one transaction |
| Rollback completion (`Compensated`) | one transaction |
| Rollback-and-restart phase transitions | one transaction per phase |

`EmitAsync` called inside a step delegate commits with that step (one
transaction); called outside a step it appends its own event in a separate
transaction. Only the outside-a-step form is the deliberate best-effort
exception.

## What fork copies

A fork copies the source run's workflow contract, serialized input, output type,
and every **completed** step outside the selected invalidation boundary, plus
their dependency edges. Pending, running, waiting, and failed steps re-execute.
The source run is never modified. Source metadata is copied; source deadline is
deliberately **not** inherited. The fork records `SourceRunId` lineage.

## What rollback guarantees

- Rollback replays the workflow definition against committed step results to
  re-bind compensation delegates, then executes only those delegates in reverse
  dependency order. It never re-runs forward operations.
- On success the run reaches `Compensated` (terminal). A failing compensation
  leaves the run `Failed` and claimable by a later rollback attempt; already
  completed compensations are reused.

## What the deadline applies to

- A run-level `Deadline` bounds the whole run: a run claimed after its deadline
  fails with a timeout instead of executing.
- A child's effective deadline is the earlier of the parent's deadline and the
  explicit `ChildRunOptions.Deadline`, so a child cannot outlive its parent.
- `WaitForCompletionAsync`'s deadline is caller-side only; it bounds how long the
  caller waits, not the run.
- Step-level `ExecutionTimeout` bounds a single step attempt.

## Dependency topology

- Dependencies are recorded transactionally at claim time. Self-dependencies are
  rejected in code and by a database `CHECK`. Adding an edge that would create a
  cycle, or adding dependencies after a step completed, is rejected.
- Restart invalidation uses the recorded dependency graph (`Dependents` mode) and
  bumps the fencing generation so stale workers can never commit to the
  restarted run.
