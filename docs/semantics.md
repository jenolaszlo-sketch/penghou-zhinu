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
- A class-based step resolves a fresh implementation and DI scope for every
  attempt. The scope is asynchronously disposed before successful completion is
  committed. A disposal failure is an attempt failure and follows the same
  retry policy.

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
| Idempotent signal buffer + `signal-sent` event + receipt | one transaction |
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

Class-based forward steps call `WorkflowStepContext.EmitAsync` for the same
atomic behavior. The event is buffered until the scoped implementation and its
scope dispose successfully; a failed attempt discards it. Event emission is not
available from manually constructed or compensation step contexts.

Class-based `FanOutAsync` uses one typed step reference and assigns durable keys
from the stable input index. Items execute independently and results preserve
input order. Reordering inputs changes their durable identity, so callers must
sort or otherwise stabilize inputs before invoking fan-out.

## Durable state loops

`WorkflowContext.LoopAsync` is sequential state-dependent repetition. It is
not an alias for `FanOutAsync`: iteration `n + 1` consumes the state committed
by iteration `n`, while fan-out items are independent siblings.

The continuation condition is evaluated before each body execution. Its result
is a durable step whose input binds the typed state and configured maximum
iteration count. A true decision permits that iteration's body. A false
decision commits the final loop result under the caller's logical loop key.

Body operations must use the supplied `WorkflowLoopIteration<TState>` surface.
Zhinu gives each operation a stable key containing the loop identity, one-based
iteration number, and body-step name. Each body step depends on that
iteration's condition decision. A successful body returns a scoped
`LoopBodyOutcome<TState>` created with `Continue(nextState)` or
`Break(finalState)`. The iteration commit depends on every body step used
through that surface and atomically persists both the selected disposition and
typed state. The following condition depends on a continue commit. A break
commit instead completes the loop normally without evaluating another
condition. These edges make dependency-aware restart preserve earlier
iterations while invalidating the selected operation, its state transition,
all later iterations, the loop result, and downstream work.

The iteration boundary uses the ordinary fenced step-completion transaction.
State output, step completion, the `loop-iteration-committed` event, and any
events emitted by that commit operation are atomic. A stale worker cannot
advance the loop after lease or generation loss. A crash before the commit
replays the same iteration and reuses completed body steps. Before entering a
body, Zhinu checks for an already completed iteration commit; a crash after the
boundary therefore reuses its state and continue/break disposition without
re-entering the body delegate.

Outcomes belong to the exact iteration context that created them. Returning an
outcome created by another iteration or loop is rejected as workflow state
corruption, even when both scopes use the same state type. Exceptions,
cancellation, and retry remain Zhinu's failure mechanisms; failure is not a
successful loop-control outcome. Nested loops require parent-scoped durable
identity and are not supported by the initial unscoped loop API.

Worker interruption before an iteration commit leaves no committed loop
transition. Recovery reuses any completed body steps and commits the outcome
once. Interruption after the commit reuses its state and disposition without
re-entering the body. Because the event is committed in the same transaction,
there is exactly one `loop-iteration-committed` event for the surviving step
revision. A worker whose run generation has been replaced cannot commit either
the disposition or its evidence. Compensation replay is intentionally
different: it re-enters body declarations to rebind compensation delegates,
but each forward step still returns its persisted result.

After the maximum number of bodies has committed, Zhinu evaluates the condition
once more. False completes normally. True commits one replay-safe
`loop-limit-exceeded` event and fails with `LoopLimitExceededException`; the
last state is never silently returned as success.

Loop and body names use only ASCII letters, digits, `_`, `-`, and `.`, with a
maximum length of 128 characters. Zhinu reserves `$loop/` for generated
condition, body, commit, and limit step keys. The caller's loop key is also the
durable final-result step key, making it the dependency target for work after
the loop.

Code-first condition delegates must be deterministic and side-effect-free.
Code implementation compatibility remains the workflow host's responsibility;
compiled adapters such as Fuwen additionally bind condition and loop semantics
into their definition fingerprint. Large loop state should use immutable
artifact references rather than embedding artifact bytes in workflow state.

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
- Class-based compensation resolves a new implementation in a new scope for
  every attempt. It receives the original persisted step input and committed
  output; no state from the forward step instance survives.
- Compensation is an explicit capability through
  `ICompensatingWorkflowStep<TInput, TOutput>`. Enabling it for an ordinary
  `IWorkflowStep<TInput, TOutput>` fails before the forward operation executes.
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
