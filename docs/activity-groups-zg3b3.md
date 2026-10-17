# ZG-3b.3: nested native repetition

## Implementation note — recorded before coding, 2026-10-17

Verified clean `codex/zhinu-activity-groups` at
`ef6d2fe3bf8d7490d5e6e89fbd5406f457f85c7f`. Inspected the occurrence encoder,
typed loop scopes/iteration wrapper, native commit/final replay, group binding,
SQLite ancestry/membership persistence, fan-out/fork profiles, qualification,
handoff and roadmap before choosing the implementation.

Native nested loops already retain `DurableLoopScope.ParentIteration`.
`ActivityGroupOccurrence.Coordinate` walks that full typed chain and serializes
it outermost first, using existing one-based iteration numbers. For example,
`[{"loop":"outer","iteration":2},{"loop":"inner","iteration":1}]`
differs from the same inner position beneath outer iteration 1. The unchanged
v1 occurrence hash includes RunId, group key, explicit parent occurrence ID and
these canonical bytes. Payloads, attempts and execution generations are excluded.
`WorkflowLoopIteration.StepAsync` supplies the actual typed scope to validation;
prefix validation prevents a sibling outer/inner occurrence from binding there.

Inspection found no lost lineage in this path and no missing durable field.
The minimum extension is to make this existing composition an explicit supported
profile with executable nested workflow fixtures, identity/retry/restart proofs,
fresh-process barriers and an isolated package consumer. No new identity encoder,
runtime scheduler, public API, table or schema migration is planned. Schema **9**
already stores the complete coordinate and immediate parent; point reads validate
the reconstructed ancestry. Existing root and single-loop identities must remain
byte-for-byte stable. Qualification will stop if a product counterexample appears.

The fixture uses a fixed native outer loop of three iterations and a fixed inner
loop of two, with outer/inner/leaf definitions. Grouped entry activities provide
real durable materialization boundaries; factories alone do not persist groups.
The six leaf activities have identical input. Completion follows native inner
commits/finals, then outer commits/final and their dependency edges; no aggregate
group completion or absence-of-runnable-work heuristic is introduced.

Planned deterministic hard-exit boundaries: outer entry completed before inner
creation; inner entry claimed before leaf; leaf claimed but unfinished in early
and later outer iterations; leaf completed before inner commit; inner final
committed before outer commit; outer commit completed before the next iteration.
Capture durable records before recovery and resume in a separate fresh worker,
without sleeps. Compare existing IDs/parents/memberships and completed step rows,
then prove exactly six leaf occurrences and the full immediate-parent chain.

Retry applies to native activities, not orchestration delegates: test both an
inner leaf and an outer entry activity. Selective restart preserves logical
occurrences while native invalidation creates new step revisions. Fixed loop
state/conditions reuse native durable selection semantics; no dynamic item-set
mutation or recomputation is added. Grouped fan-out remains root-only, and grouped
fan-out/fork combinations remain rejected. Nested-loop fork composition is not a
newly qualified profile here; retain the existing fork regression coverage.

Evolving selection remains deferred after ZG-3b.3.
