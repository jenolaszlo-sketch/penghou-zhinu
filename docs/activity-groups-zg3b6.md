# ZG-3b.6: consolidated dynamic durability boundary

Date: 2026-10-18. Starting checkpoint:
`bbd98d5bd330e360d64d8b77d0205a6b6828a136`, verified clean before the audit.
Runtime/package source remains `3ca11057c5c43463bc9c82304058d68e60b3433e`.
This checkpoint consolidates the five selected native profiles. It adds focused
negative tests and an evidence verifier, not runtime code or another identity
model. See [qualification](qualification/activity-groups/zg3b6.md) for the exact
audit/test source and retained artifact checks.

> **Nested repetition required no runtime algorithm, schema, or identity redesign.**

**Static/restart correctness does not imply fan-out/fork correctness.** Likewise,
separate successful constructs do not qualify every composition. The supported
union below is the bounded ZG-3b exit; the wider activity-group milestone and
unsupported compositions remain open.

## Supported native profiles

| Surface | Qualified boundary | Evidence |
| --- | --- | --- |
| Static groups and repetition | Optional Logical/Macro forest, explicit innermost Work/Control membership, root/typed-loop occurrence factories, exact revision history. Fixed native 3×2 nested repetition preserves full outer-first lineage and immediate parents through retries/restart/process loss. | [Foundation](qualification/activity-groups/zg2-3a.md), [nested repetition](qualification/activity-groups/zg3b3.md) |
| Root native fan-out | All four functional/class keyed/positional overloads. One selected item is one durable step. Explicit completed selection/dependency, atomic whole-plan admission, isolated item membership, immutable selection and exact current-revision closure. Caller result order remains native. | [ZG-3b.1](qualification/activity-groups/zg3b1.md) |
| Native forks | Separate destination runs, destination-owned copied ancestry, unchanged root/loop coordinates, source provenance, native reuse/reexecution and authority continuity. There is no new in-run fork/join interpreter. | [ZG-3b.2](qualification/activity-groups/zg3b2.md) |
| Evolving selection | Distinct stable root fan-out/selection keys per explicit durable epoch. Pinned historical schedule and ordered selections replay; new unselected epochs may consult current selectors. Old unfinished scopes remain obligations. Reappearance/identical values do not alias old occurrences. | [ZG-3b.4](qualification/activity-groups/zg3b4.md) |
| Native children | Optional parent `StartGroup`/`WaitGroup` bind existing start/wait steps, with both prevalidated before start. Child owns its manifest, run and group tree. Native retry/restart/fork provenance, authorization, successful cancellation and parked/ready/completed wait recovery remain intact. | [ZG-3b.5](qualification/activity-groups/zg3b5.md) |

All five profiles use SQLite **schema 9** and the existing **5→6→7→8→9** migration
chain. Manifest v1 and root/loop occurrence v1 remain fixed. Fan-out uses the
existing v2 native scope + key/index frame; epochs author new scope keys within
that encoding. Forks use destination RunId; children retain the native hash of
parent RunId, invocation key and start revision. No new fork, epoch or child
group hash field is introduced. Attempts, leases and execution generations are
fences/history, not new occurrence identity inputs.

## Explicit rejection contracts

| Invalid request | Guard and observable rejection proof |
| --- | --- |
| Direct `StepOptions.Group` on native fan-out | `WorkflowContext.RejectGroupedFanOut`; all four overloads reject before item callbacks/resolution/step claims. Use `FanOutGroup` for the supported item declaration. |
| `FanOutGroup` on an ordinary `StepAsync` | `WorkflowContext.StepCoreAsync`; rejects before the ordinary step claim, callback, occurrence/membership or expansion admission. Consolidation adds null/root-parent cases. |
| Explicit loop-coordinate parent in grouped fan-out | `ActivityGroupFanOutPlan.Create`; root parent requirement rejects both keyed and positional plans. |
| Competing typed loop and fan-out scope for one claim | `ActivityGroupRuntime.ValidateBinding`; rejects the conflicting explicit coordinates. |
| Forking a run marked with grouped fan-out evidence | Engine `ForkAsync` and grouped store fork path reject before destination creation or source mutation. Consolidation adds both engine overload routes; store transaction tests remain separate. |
| Changing an admitted plan under an old fan-out scope | Immutable plan/admission and exact claim checks reject changed selection/content/parent/contract. Restarting selection does not authorize changed old membership. New explicit epochs use new scope keys. |
| Invalid child boundary binding | Foreign run, wrong manifest or iteration-coordinate binding on the untyped API rejects before child start/wait claims. Parent and child occurrence pointers cannot cross runs. |
| Missing capability, changed registration/pin, stale owner/generation | Existing admission, claim, fork and expansion fences reject without authorized effects or partial group writes, within each qualified provider/profile. |

Runtime checks validate explicit runtime identities. They cannot infer arbitrary
C# lexical nesting: an untyped `WorkflowContext.FanOutAsync` called inside a loop
without an explicit loop-coordinate parent is not universally detected as nested
fan-out. Nor does an untyped StartChild call establish a typed iteration scope.
Describe these combinations as **not qualified**, rather than asserting a global
syntactic rejection. Grouped fan-out/fork copying, in contrast, has an explicit
run marker and both engine/store rejection gates.

## Not qualified by this exit

- Arbitrary mixed loop/fan-out/child composition, nested fan-out coordinates or
  independently durable multi-step item bodies. One durable native item step
  must not be presented as several independently durable macro children.
- Typed iteration/fan-out child lowering or a new typed child invocation API.
- Changed-plan replacement within an old scope, implicit epoch inference,
  automatic retirement/cancellation of excluded work, or an active-only reducer.
- Atomic subtree cancellation. Native cancellation is a sequence of existing
  durable operations; the successful path proof does not turn it into one commit.
- Coherent hierarchy/pagination/child drill-through queries, aggregate lifecycle
  and progress, group operational controls or UI.
- Fuwen macro syntax/adapter lowering, cross-repository qualification,
  Linux/remote CI, package publication or mixed old/new schema writers.

These are explicit limits, not silent passes or newly changed native semantics.
Any future Fuwen selected profile requiring a missing composition must implement
and qualify it, or reject it before execution. Native positive fixtures alone
cannot close Fuwen's per-child durability contract.

## Evidence reuse and audit

`eng/Verify-ActivityGroupQualification.ps1` verifies all five historical ledgers
against their exact Git source, local TRX and local package artifacts. Source
checks preserve Git blob bytes or the corresponding Windows CRLF checkout;
they do not trim or reinterpret source text. It checks all 55 full-suite result
files and 35 nupkg hashes/repository commits, profile totals and framework
package/binary/schema receipts. Package version `0.2.0-preview.4` alone is not an
artifact identity: each tranche has its own source and seven package hashes.

The current runtime and consumer probes match the latest qualified source.
Existing tests also remain unchanged; only four new composition cases are added
and run on both frameworks. Retained full-suite evidence is **1,639 tests**;
the **eight new focused executions** are reported separately. They are not a
new 1,647-case full-suite run. No new process-loss workers are needed for these
early rejection guards.

Historical tranche-specific recovery records total 68 positive receipts, two
negative omitted-history cases and 146 worker starts. This is an audit of prior
executions, not 146 workers started by consolidation. Repeated older workers
inside later cumulative suites are not added again to this tranche ledger.

## ZG-4 entry contract

Proceed next to **ZG-4 hierarchy queries**, preserving these boundaries:

1. Return definition intent separately from entered runtime occurrences; do not
   synthesize an entered root or materialize every definition.
2. Preserve run-local occurrence IDs, immediate parents, direct membership,
   Work/Control roles, exact current/history revisions and native source lineage.
3. Link a child run for drill-through; never merge its tree into its parent or
   reinterpret a fork's reused child as destination-owned child history.
4. Read group/step/wait/expansion facts at one coherent boundary. Existing point
   reads are diagnostic capabilities, not a multi-call snapshot guarantee.
5. Expose coverage honestly. Fan-out closure is revision-aware; a prefix of rows
   is not the selected set, and a definition or closed item list is not an
   aggregate completion/progress calculation.
6. Preserve the planned bounded pagination, explicit unsupported/not-found/limit/
   resync outcomes and authorization-safe projection contract. Reader API names
   and snapshot protocol require their own implementation note and qualification.

ZG-5 aggregate state/progress follows ZG-4. ZG-6 Fuwen and ZG-7 cross-repository
qualification remain later gates. Consolidation freezes the supported durability
boundary; it does not implement any of these reader or integration features.
