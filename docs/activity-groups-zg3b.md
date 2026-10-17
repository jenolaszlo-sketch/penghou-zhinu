# ZG-3b: dynamic occurrence durability

Status: **ZG-3b.1 root native fan-out and ZG-3b.2 grouped native forks implemented**;
the remaining independent verticals below are open. See the
[3b.1 contract](activity-groups-zg3b1.md), [3b.2 contract](activity-groups-zg3b2.md)
and their construct-specific evidence. The next proof is **3b.3 nested repetition**,
combining qualified primitives without evolving selection.
Decision recorded 2026-10-16. Start from qualified checkpoint
`33c051ed4907178e13d273e930d1f63342321f3f` on `codex/zhinu-activity-groups`.
Runtime foundation: `130e927f2d08f803e69a2e5f4c437e4e1bbacb30`.
Read the [handoff](activity-groups-handoff.md), [foundation contract](activity-groups-zg2-3a.md)
and [foundation evidence](qualification/activity-groups/zg2-3a.md) first.

## Objective and order

Extend the established occurrence model across dynamic execution surfaces.
Complete ZG-3b before ZG-4 hierarchy/query projection, then ZG-5 aggregate state
and progress, ZG-6 Fuwen handoff/integration and ZG-7 cross-repository qualification.
Queries and progress must consume a qualified occurrence model; Fuwen must not
become coupled to temporary native-runtime limitations.

**Static/restart correctness does not imply fan-out/fork correctness.** The
1,339 passing foundation tests remain evidence for their original scope. They
are not evidence that any construct below is already supported.

## Required verticals and proof

Internal sequence: **3b.1 fan-out → 3b.2 forks → 3b.3 nested repetition/parents →
3b.4 partial/evolving expansion and recovery → 3b.5 child boundaries → 3b.6
consolidated qualification**. The first profile requires a completed explicit
selection step and an immutable selected set at root execution scope. Its
admission/partial-item/closure process proofs do not close broader 3b.4.
The fork profile uses existing separate destination runs and unchanged root/loop
coordinates, with destination-owned copied ancestry. It has no new in-run
fork/join coordinate. Source grouped fan-out evidence remains a rejected combined
profile until enumeration/closure rebinding is qualified.

| Dynamic surface | Required behavior and qualification |
| --- | --- |
| Grouped fan-out | One definition yields distinct item occurrences with correct explicit parents. Use existing native keys and deliberately tagged positional identity; never parse display or dotted step keys. Persist enumeration/selection and current-revision closure alongside occurrence claims. Prove reorder stability for keyed items and concurrent sibling isolation. |
| Grouped forks | The destination owns a manifest, occurrences and exact-revision memberships under its new RunId. Copied members resolve to destination parent occurrences, preserve existing producer/source provenance, and never retain source-run group pointers. Prove atomic creation and unchanged source state. |
| Nested repeated groups | Repeat one inner definition across at least two outer and two inner coordinates. Prove distinct occurrences, correct parent for each repetition, and no aliasing after replay or unrelated sibling reordering. |
| Restart during partial fan-out/fork execution | Terminate inside the affected transaction and after committed partial work, then restart/recover. Retain committed work and history, rebuild only the correct current occurrences, and prevent stale generations from extending or sealing invalidated expansion. A failed fork transaction must leave no partial destination. |
| Resume/recovery identity | Reconstruct in a fresh process before running callbacks; compare occurrence IDs, coordinates, parent IDs and exact revision memberships. Retry/reclaim must not invent occurrences, and recovery must reuse committed activity results. Unfinished attempts retain existing retry/effect semantics. Apply this proof to each supported dynamic construct, not just a static fixture. |
| Child-workflow boundaries | Qualify parent start/wait membership, the child's separate tree, existing deterministic child IDs, reuse/restart/cancellation and interrupted boundary recovery. Parent occurrence pointers stay in the parent run; child occurrences stay in the child run. Existing authority and child execution semantics remain intact. |

## Evidence and exit gates

For each vertical, retain exact branch/base/implementation commits, framework and
schema versions, before/after durable records and callback/effect receipts. Cover
positive repeated execution, malformed or unsupported shapes, stale writers,
and real process termination at the relevant transaction/commit boundaries.
An in-process reopen alone cannot close the recovery gate.

Inspect durable records before allowing recovery callbacks to run. Capture the
run, definition key, occurrence coordinate/ID, explicit parent, step ID/revision,
attempt and membership, plus the selected/enumerated set and closure evidence
where applicable. A prefix of committed item rows is not a complete enumeration.
Changed control revisions must not make historical closure current again.

Publish separate SUPPORTED and NOT YET QUALIFIED lists for each construct/profile.
Keep the existing grouped fan-out/fork rejection until the corresponding complete
durable vertical passes. Partial success must not imply support for arbitrary
nested combinations or all child/fork operations. Update the full
[qualification matrix](activity-groups-qualification.md) without closing query or
progress cases using persistence tests alone.

Keep all existing ungrouped, authorization, control-flow, fencing, restart,
snapshot/event, retention and binary/package compatibility checks green. If the
schema changes, reserve its next free version after inspecting the branch and
qualify retained rows, transactional migration and old-worker rejection. Schema
8 is the starting point, not an instruction to change the schema unnecessarily.

## Constraints and handoff

Extend the foundation; do not redesign its definition/occurrence split, optional
membership, explicit ownership, manifest pins, existing workflow control flow,
step/child/operation identities or authority boundaries. Preserve manifest v1 and
existing occurrence byte vectors. Any additional coordinate encoding must be
explicitly versioned/compatible rather than changing historical IDs or backfilling
old runs. Add typed item scope only together with its durable expansion evidence.

No hierarchy/query API, aggregate progress, UI or Fuwen integration belongs in
ZG-3b. End with construct-specific evidence and a fresh-agent handoff that records
exact commits, compatibility constraints, unsupported combinations and the next
checkpoint. A different model should continue from repository artifacts without
depending on chat history.
