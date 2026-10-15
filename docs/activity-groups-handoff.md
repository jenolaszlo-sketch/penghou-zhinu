# Activity groups: next coding agent handoff

Prepared 2026-10-15. This file is self-contained; no chat history is required.
**Checkpoint: design and planning prepared; implementation has not started.**

## Objective

Preserve Fuwen's user-defined macro structure as optional durable nested Zhinu
groups. Activities continue to execute through the existing workflow/control-flow
semantics. A generic operator can inspect a hierarchy without knowing what
SupervisedCoding, TDD or AlienProcess42 means. Do not implement UI in this work.

## Read first

1. [Design](activity-groups-design.md): actual architecture, resolved model,
   identities, state table, persistence and Fuwen boundary.
2. [Implementation plan](activity-groups-implementation-plan.md): seven stages,
   files, invariants, tests, dependencies and non-goals.
3. [Qualification matrix](activity-groups-qualification.md): Q01-Q47 and commands.
4. [Zhinu roadmap](../ROADMAP.md) ZG milestone and sibling
   [Fuwen roadmap](../../Penghou.Fuwen/docs/roadmap.md) FM milestone.
5. [Store contract](store-contract.md), [API conventions](api-conventions.md),
   [public API policy](public-api-policy.md), [releasing](releasing.md), and
   [preview.3 candidate notes](releases/0.2.0-preview.3.md).

## Repository state and provenance

Repositories are siblings. Inspected local roots were
`C:/Users/Laszlos/source/repos/Penghou.Zhinu` and
`C:/Users/Laszlos/source/repos/Penghou.Fuwen`; do not edit scratch copies in Solo
as if they were the product repositories.

| Repository | Inspected branch/base commit | State on entry |
| --- | --- | --- |
| Penghou.Zhinu | `main`, `99d9d78e863badb45571f1f3799d79a6fb88c39f` | Modified `ROADMAP.md` from earlier macro planning; separately modified `docs/ideas.md`. |
| Penghou.Fuwen | `main`, `603b2e78d3f3ff33e2152641ab0ca016a120552e` | Modified `docs/roadmap.md` from earlier macro planning. |

The intended handoff change consists only of Zhinu `ROADMAP.md`, the four
`docs/activity-groups-*.md` documents, and Fuwen `docs/roadmap.md`. These are
documentation-only commits on the same branches; no runtime, schema, package
reference or tests are changed **by this task**. `docs/ideas.md` is unrelated existing user work
and must remain untouched/uncommitted by this task. No push or publication is
part of this handoff. No applicable repository/docs `AGENTS.md` was found during
inspection; check again if the destination checkout differs.

During final preparation, concurrent uncommitted authority-subject work appeared.
Observed modified source files (all excluded from the group documentation commit):

- `src/Penghou.Zhinu.Sqlite/Persistence/SqliteStoreSupport.cs`
- `src/Penghou.Zhinu.Sqlite/Persistence/Workflows/Commands/InsertRunCommand.cs`
- `src/Penghou.Zhinu.Sqlite/SqliteDatabase.cs`
- `src/Penghou.Zhinu.Sqlite/ZhinuSchemaCompatibilityException.cs`
- `src/Penghou.Zhinu.Sqlite/PublicAPI.Unshipped.txt`
- `src/Penghou.Zhinu/WorkflowEngine.cs`
- `src/Penghou.Zhinu/WorkflowEngineCapabilities.cs`
- `src/Penghou.Zhinu/WorkflowRun.cs`
- `src/Penghou.Zhinu/PublicAPI.Shipped.txt`
- `src/Penghou.Zhinu/PublicAPI.Unshipped.txt`

New untracked test observed: `tests/Penghou.Zhinu.Sqlite.Tests/AuthoritySubjectBindingTests.cs`.
This is an observation, not an exhaustive final inventory of another active task.
The changes add an opaque durable authority-subject binding, alter public start
signatures/fork propagation, and use schema 7 with an additive 6-to-7 migration.
They were inspected for overlap, not reviewed/tested for correctness here. Do not
revert, stage or commit them as group work. Re-read current Git state and coordinate
the baseline before implementing ZG-1; groups provisionally use schema 8 after
authority work lands. Preserve that binding independently of group identity.

Resolve the documentation commits and current state before implementation:

```powershell
git status --short --branch
git log -1 --format="%H %s" -- docs/activity-groups-handoff.md
git diff 99d9d78e863badb45571f1f3799d79a6fb88c39f -- ROADMAP.md docs/activity-groups-design.md docs/activity-groups-implementation-plan.md docs/activity-groups-qualification.md docs/activity-groups-handoff.md
git -C ../Penghou.Fuwen status --short --branch
git -C ../Penghou.Fuwen log -1 --format="%H %s" -- docs/roadmap.md
```

The handoff's own immutable identity is the commit returned by the second
command, avoiding a self-referential embedded commit hash. If source moved since
the base commits above, reconcile the changed components before relying on this
design. Repository state is intentionally not described as wholly clean while
the unrelated ideas edit and concurrent authority work exist.

## Decisions already settled

- Groups annotate execution; they do not schedule, authorize, retry or become
  child workflows. Membership is optional; no synthetic persisted root.
- Use a group forest with direct innermost step membership, not activity parents.
  Logical/Macro are provenance kinds with identical runtime treatment.
- Separate immutable group definitions from run-local occurrences. Identity uses
  run, manifest, GroupKey and explicit loop/fan-out coordinates. Attempts and
  lease generations are not identity. Step revision history remains authoritative.
- Child invocation `:start`/`:wait` steps can belong to the parent group; the
  child run owns its own tree. Reuse the existing child-run relationship.
- Use existing `StepStatus` plus projected reason/coverage/expansion metadata.
  Blocked is a display reason. Apply the ordered table in the design, not model
  inference or a new persisted group state machine.
- Persist selection/closure evidence where existing control boundaries cannot
  reconstruct it. Known children are not the total while expansion remains open.
  Group counts/status are derived from current revisions in one read boundary.
- Store a versioned companion manifest bound to the admitted plan/run. Preserve
  historical executable canonical bytes and fingerprints. No blind argument
  copying; safe opaque macro provenance only.
- Add optional store/reader capabilities and additive schema migration. No new
  required contract for an old custom store running ungrouped workflows.
- Reuse atomic snapshots, durable events/cursors and the existing Fuwen step
  mapper; do not invent a second event log or client step-key parser.

## Important facts and traps from the code inspection

1. `WorkflowDefinition` is Name/Version, not an executable graph. Durable work is
   `WorkflowStepRun` keyed by run/StepKey/Revision, with Attempt tracked separately.
   Do not add GroupId to an activity descriptor and assume that models invocations.
2. Native nested loops already have typed iteration identity and condition/body/
   commit/final steps. These relationships do not justify activity parent links.
3. Native keyed fan-out validates keys and creates bounded step batches; it has
   no durable full enumeration manifest. A crash can leave only a prefix visible.
4. Fuwen's current fan-out body is one durable item step. Its mapper records
   covered declared nodes. A completed item can carry a failed outcome payload.
   Never manufacture independent child durability/status by decoding that in
   Zhinu. Macro fan-out needs a versioned adapter extension or explicit rejection;
   rejection leaves full Q45/FM-ZG completion open.
5. Fuwen currently references Zhinu **0.1.0-preview.14**, while this Zhinu checkout
   is **0.2.0-preview.3 candidate**. Source availability is not package compatibility
   or publication evidence. Qualify exact candidate packages and old consumers.
6. The committed schema 6 already upgrades schema 5 transactionally; concurrent
   authority work now uses schema 7. The older exception comment
   saying preview schemas never migrate is stale. Reserve the next schema number
   only after checking the current branch; preserve the 5→6→7 migration chain.
7. Atomic M5 snapshots/watermarks/event pages already exist. Current run snapshots
   do not include approval-pending rows; group projection must read safe approval
   facts in the same transaction. Generation/operation transitions are snapshot-only,
   so an event watermark alone is insufficient for multi-request page consistency.
8. Existing helpers construct fresh StepOptions (including loop wrappers and child
   boundaries). Audit propagation explicitly; group metadata must not erase retry,
   dependency, authorization or compensation settings. No blanket runtime rewrite.
9. Do not close a group from `finally`/Dispose. Failure/cancellation/restart must
   not leave stale success or convert a partial fan-out into an apparently complete
   smaller one. Membership and closure writes require existing fences.

## What to implement next

Start **ZG-1**, not the whole feature at once:

1. Establish a fresh baseline with the commands in the qualification document.
2. Add immutable bounded manifest/definition/provenance records and forest
   validation in core, plus versioned identity/compatibility fixtures.
3. Exercise optional membership/no-root semantics and invalid hierarchy cases.
4. Sketch the additive registration/store capability API; update public API
   baselines for actual additions. Keep group execution disabled until durable
   storage support is qualified.

The next checkpoint is an inspectable ZG-1 diff with Q01-Q09/Q24-Q26 contract
coverage, deterministic vectors and legacy public-surface tests green. At this
checkpoint **do not change SQLite schema, runtime scheduling or package pins**.
Then combine ZG-2/3 into a narrow durable vertical, implement the ZG-5 reducer
before freezing ZG-4 read DTOs, and finish ZG-6/7 with Fuwen.

Remaining bounded decisions: exact canonical bytes/encoder golden vectors,
public method/capability names, resource limits, read-cache TTL/capacity and the
free next schema number. Fuwen owns syntax, argument/output semantics and its
admission binding. Do not quietly settle those by adding methodology primitives.

## What must remain green and how to prove progress

Keep the existing core/public-surface, SQLite loop/fan-out/restart/child,
authorization/fencing/migration, generation, snapshot/event and retention suites
listed in the qualification document. Preserve package closure and old-binary
consumer probes from CI. Both target frameworks and Windows/Linux matter.

For durability stages, extend the existing separate ProcessProbe executable.
Terminate before/after commits, reopen in a fresh process, inspect before executing
recovery, then resume and compare IDs/state/closure/provenance/effect receipts.
An in-process reopen or mocked store alone does not prove the acceptance gate.

Documentation preparation verified links, referenced source/test paths, Markdown
structure and diff whitespace. It did **not** run the runtime suites or implement
any group behavior. No test count in older roadmap notes is a fresh baseline.

Do not redesign workflow control flow, inference protocols, Hufu authority,
artifact reuse, compensation, child identity, existing step keys or historical
Fuwen fingerprints. Do not add UI, methodology-specific behavior, group commands,
general metaprogramming, or unconditional legacy-data migration/backfill.
