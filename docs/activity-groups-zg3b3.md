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

## Supported profile and identity

The [qualification record](qualification/activity-groups/zg3b3.md) binds this
profile to exact source, frameworks and gates. This slice makes the existing
typed-loop composition an explicitly qualified contract; runtime identity,
native loop execution and persistence algorithms are unchanged.

Use `outer.LoopAsync` for the inner loop and `inner.StepAsync` for its activities.
Create an outer group through the outer iteration, then the inner group through
the inner iteration with that outer occurrence as explicit parent. A leaf group
created through the inner iteration points to that inner occurrence. A single
definition of each kind produces three outer, six inner and six leaf occurrences
in the fixed 3×2 case. Inner/leaf coordinates share their two loop frames; their
different group keys and explicit parents distinguish the groups. Local inner
position 1 under outer 1 cannot alias local inner position 1 under outer 2.

Factories are references, not independent completion or durable entry records.
The first actual member claim persists ancestry/membership atomically. The
qualification fixture uses ordinary grouped entry activities to expose durable
outer/inner entry barriers. The package consumer also proves lazy materialization
of the complete parent chain from leaf claims without separate entry activities.

There is no item-selection/addressing mechanism added for loops. Native typed
one-based iteration frames stay distinct from fan-out's tagged zero-based indices
or keyed v2 frame. Manifest pins, v1 hash fields, canonical coordinate bytes,
existing root/single-loop vectors, step identities and retry policies stay intact.

## Retry, restart and completion

Native activity retry changes attempt while preserving the exact step revision,
occurrence, coordinate and parent. Tests force an inner leaf and an outer entry
activity to fail once; completed siblings receive no new callback. Native loop
orchestration delegates have no separate retryable activity identity, so an
independent outer-body retry policy would invent new semantics and is not added.

Selective restart at a later inner leaf invalidates its normal dependent native
control work; restarting a later outer commit reuses completed nested work where
the native dependency graph allows it. Historical memberships remain readable,
new step revisions retain the same occurrence, and completed work outside the
invalidation set remains unchanged. Changing selection/inputs is outside this
fixed profile.

Completion is entirely native: each inner iteration commit depends on its body,
each inner final follows its terminating condition/commit, each outer commit
depends on its inner final, and the outer final follows its terminal condition.
Recovery replays committed state and never infers success from missing runnable
work. Boundary/event assertions cover all six inner and three outer commits,
three inner finals and one outer final, once per completed revision including
retained selective-restart history. Groups acquire no separate aggregate status.

## Process and query boundaries

Seven hard-exit modes cover outer entry, inner entry, early/later unfinished leaf,
completed leaf, inner final and outer commit. Each has a fresh crash worker and a
second fresh recovery worker on .NET 8/10. Before recovery, inspect occurrence
IDs/coordinates/parents, exact step revisions/attempts/status/results and membership.
After recovery, assert all prior occurrence/membership records remain identical,
completed steps are reused, unfinished steps keep ID/revision and increment
attempt, future iterations continue with distinct lineage and exactly six leaves
exist. Raw occurrence counts exclude hidden duplicates/orphans. No sleep controls
the barriers; the recovery worker uses one consistent advanced clock to expire
the dead worker's lease and make recovered steps immediately eligible.

Existing point reads reconstruct leaf → inner → outer through immediate parent
IDs and validate the full canonical coordinate. They do not collapse equal local
positions under different outer iterations. Coherent public hierarchy queries,
snapshots, aggregate state and progress remain ZG-4/5 work.

## Composition limits and next work

Grouped fan-out remains its root-only fixed-selection profile. Keyed and positional
fan-out under a loop parent reject during plan validation; combining typed-loop
and fan-out coordinates rejects before a claim. Direct `Group` on fan-out remains
invalid. Grouped fan-out/fork copying remains explicitly rejected in engine/store.
Existing grouped-fork destination lineage and single-loop reuse regressions remain
required; nested repetition/fork composition is not newly qualified by this slice.
No existing native fork contract is widened or narrowed here.

Schema remains **9**. No public signature, migration, package dependency or binary
contract changes. Evolving selection, added/removed/reordered/recomputed dynamic
sets, broader partial dynamic expansion, child-boundary semantics, arbitrary
mixed constructs and Fuwen lowering are outside this profile. Continue with
ZG-3b.4, then 3b.5 children and 3b.6 consolidated qualification before ZG-4.

Evolving selection remains deferred after ZG-3b.3.
