# Activity groups: staged implementation plan

Date: 2026-10-18. Status: ZG-1, the ZG-2/3a durable foundation and
[ZG-3b.1 root native fan-out](activity-groups-zg3b1.md) and
[ZG-3b.2 grouped forks](activity-groups-zg3b2.md) and
[ZG-3b.3 fixed nested repetition](activity-groups-zg3b3.md) and
[ZG-3b.4 explicit immutable selection epochs](activity-groups-zg3b4.md) and
[ZG-3b.5 native child boundaries](activity-groups-zg3b5.md) implemented.
The [ZG-3b.6 consolidated boundary](activity-groups-zg3b6.md) qualifies the
selected native profiles together and records remaining composition limits.
The [ZG-4 query surface](activity-groups-zg4.md) and
[ZG-5 aggregate projection](activity-groups-zg5.md) are qualified over that
bounded model; see [ZG-4 evidence](qualification/activity-groups/zg4.md) and
[ZG-5 evidence](qualification/activity-groups/zg5.md). The bounded
[ZG-6.1 static Fuwen profile](qualification/activity-groups/zg6-static.md) is now
qualified. [ZG-6.2a single-level grouped repeats](qualification/activity-groups/zg6-repeat.md)
are also qualified. [ZG-6.2b grouped fan-out bodies](qualification/activity-groups/zg6-fanout.md)
now qualifies per-operation adapter lowering. Next are broader dynamic
composition and adapter closure/progress gates within ZG-6.
See the [foundation evidence](qualification/activity-groups/zg2-3a.md),
[foundation scope](activity-groups-zg2-3a.md),
[ZG-1 contract](activity-groups-zg1.md) and [ZG-1 evidence](qualification/activity-groups/zg1.md).
Normative baseline: [design](activity-groups-design.md).
Proof requirements: [qualification](activity-groups-qualification.md).
Start here on a fresh coding session: [handoff](activity-groups-handoff.md).

## Stage mapping and order

The original roadmap's broad ZG-0/1/2 buckets meant design, durable integration,
and inspection/qualification. This plan refines them into ZG-1 through ZG-7;
the identifiers below supersede that broad numbering for implementation tracking.
ZG-0 is the documentation/design checkpoint, not a runtime feature completion.

User-directed order, updated 2026-10-16:

```text
ZG-1 contracts -> ZG-2 / ZG-3a foundation (qualified)
              -> ZG-3b dynamic durability
              -> ZG-4 hierarchy/queries -> ZG-5 aggregate state/progress
              -> ZG-6 Fuwen handoff/integration -> ZG-7 cross-repository qualification
```

Identity and persistence are one incremental vertical: do not publish a
memory-only group feature and call it durable. Finish the dynamic occurrence
model before exposing queries or aggregate progress. ZG-4 first projects durable
structure and facts; ZG-5 adds derived state/progress through compatible query
extensions. This replaces the earlier ZG-5-before-ZG-4 ordering. Fuwen adoption
waits for qualification of the structures its selected profile can emit.
No group feature is enabled by default.

All new file names below are proposed; existing integration anchors are exact.
Every stage must update public API baselines where applicable, preserve existing
tests, and report supported versus rejected shapes explicitly.

## ZG-1 — Definitions, optional membership contract and validation

Delivered: immutable bounded definitions/provenance/membership values, forest
validation, manifest v1 canonical bytes/digest and compatibility fixtures.
The registration/store capability is a documented sketch only: no runtime API
accepts metadata until the durable ZG-2/3 vertical can preserve it. No new
required members, execution behavior, schema changes or package pins are included.

**Scope:** immutable manifest/definition records, Logical/Macro kinds, safe
provenance, optional registration capability, bounded forest validation, versioned
manifest encoding and golden vectors. Specify Work/Control roles and nullable
membership DTOs without changing existing step execution.

**Likely components:** new `src/Penghou.Zhinu/ActivityGroups/` records/validator;
optional companion to `IWorkflowRegistration`/`WorkflowRegistration.cs`;
`StepOptions.cs` additive metadata contract; `PublicAPI.Unshipped.txt` in changed
packages. Do not add required group members to existing interfaces or constructors.

**Invariants:** no persisted synthetic root; standalone steps remain valid;
direct innermost ownership only; parent links are group links; source strings
are data; unknown macro names require no runtime extension. Manifest mutation
after registration cannot change canonical bytes or validated structure.

**Tests/probes:** Q01-Q09 and Q24-Q26; deserialize/serialize an old definition
and compare its existing fingerprint; compile an unchanged external custom
store/registration consumer. Probe depth and expansion-size limits iteratively,
including malformed forests and sensitive-provenance rejection.

**Exit:** positive/negative manifest vectors, exact public API diff, all legacy
unit/public-surface tests green, and groups still disabled without a group-capable
store. Record exact limits and canonical encoder version in the design.

**Dependencies:** ZG-0. **Non-goals:** parser syntax, registry/library distribution,
execution scheduling, schema installation, group controls or UI.

## ZG-2 — Occurrence coordinates and claim mapping

Foundation scope: explicit root/typed-loop occurrences, optional revision
membership, immutable run pin and fail-closed admission land with ZG-3a.
Direct Group on fan-out rejects. Grouped forks use an optional durable capability
to rebind copied ancestry under the existing destination RunId, preserving the
root/loop coordinate and hash domain. The additive FanOutGroup
profile in ZG-3b.1 admits bounded root native fan-out. ZG-3b.5 adds parent child
boundary grouping; broader mixed dynamic and cross-repository profiles remain later work.

**Scope:** deterministic occurrence encoder, explicit parent occurrence mapping,
run-local Work/Control membership, current/historical revision selection. Integrate
metadata plumbing without changing step keys, operation IDs or retry policy.

**Likely components:** new `ActivityGroups/` occurrence/coordinate helpers;
`WorkflowContext.cs` claim creation; `StepClaimRequest.cs`; `WorkflowStepRun.cs`
optional projection fields; `WorkflowLoopIteration.cs`, `WorkflowContext.Loops.cs`,
`DurableLoopIdentity.cs`, `WorkflowLoopReference.cs`; native keyed fan-out and
`StartChildCoreAsync`; `Context/ChildRunCoordinator.cs` boundary propagation.

**Invariants:** same tuple after process loss; no attempt/lease in occurrence ID;
new step revision retains history; sibling fan-out tasks cannot inherit one
another's group metadata. Prefer explicit immutable execution scope passing;
if an async-local convenience API is added, prove isolation across fan-out and
exception paths. Nested-loop `StepOptions` copies must propagate group metadata
without dropping existing authorization or retry settings.

**Tests/probes:** Q10-Q16, Q21-Q23, Q27; golden coordinates include nested loops,
typed fan-out keys, explicit positional keys, repeated definitions under different
parents and sibling reorder. Trace both `:start` and `:wait` membership and verify
child IDs and operation keys are byte-for-byte unchanged for equivalent old runs.

**Exit:** deterministic identities and complete claim-path map; every path either
supports optional membership or rejects it before work. Land with the smallest
ZG-3 transaction proof; do not expose a process-local-only runtime capability.

**Dependencies:** ZG-1. **Non-goals:** arbitrary C# introspection, step-key parsing
in clients, new activity parent links, widening supported Fuwen control flow.

## ZG-3 — Additive persistence, expansion evidence and restart

Bounded native profiles qualified: [ZG-3b dynamic durability](activity-groups-zg3b.md), with separate proof for
grouped fan-out, forks, nested repetition, interrupted partial execution,
resume/recovery and child boundaries. ZG-2/3a evidence is not dynamic qualification.

ZG-3b.1 adds schema 9 selected-set admission and exact-current-revision closure
for native fan-out at an existing explicit selection step. ZG-3b.2 adds atomic
grouped fork rebinding, unchanged native reuse/authority, exact revision history
and process-loss reconstruction without a schema change. ZG-3b.3 qualifies fixed
3×2 nested loops, full typed lineage/parents, activity retry, selective restart and
fresh-process recovery using the existing schema 9 and occurrence v1 model.
ZG-3b.4 qualifies evolving sets across distinct durable root epochs using existing
selection steps/native scope keys, preserving old membership and unfinished work.
It rejects in-place changed old plans and adds no implicit retirement or epoch API.
ZG-3b.5 attaches optional explicit membership to the native parent start/wait
steps and qualifies the child's independent tree through native reuse/restart,
fork provenance, authorization, cancellation and interrupted recovery. It does
not add typed loop/fan-out child lowering. Grouped fan-out/fork combinations
remain rejected. Evidence for the first fan-out profile is
[separate](qualification/activity-groups/zg3b1.md) from the foundation.
The [consolidated qualification](qualification/activity-groups/zg3b6.md) audits
each profile and closes two focused rejection-coverage gaps. No runtime changes
or generalized mixed-composition support are introduced. ZG-4 and ZG-5 now
project this bounded model; they do not extend its execution profiles.
That record must preserve: **Nested repetition required no runtime algorithm,
schema, or identity redesign.**

ZG-3a adds schema 8 atomic run/manifest and claim/occurrence/membership writes,
historical revision inheritance and real process-loss probes. It does not
establish expansion closure or aggregate completeness. ZG-3b.6 closes only the
selected native dynamic profiles; broader mixed/Fuwen and query/progress matrix
requirements remain open under their explicit later gates.

**Scope:** optional store capability; run-bound manifest installation; occurrence,
membership and expansion-evidence persistence; transactional migration; restart,
fork/child creation and retention integration. Implement a minimal nested static
group vertical first, then loops/fan-out using the same durable contracts.

**Likely components:** new core `IWorkflowGroupRepository` capability and SQLite
`Persistence/Groups/` commands/queries; `SqliteDatabase.cs`,
`ZhinuSchemaCompatibilityException.cs`, `SqliteWorkflowStore.cs`;
`SqliteStepRepository.Claims.cs`, `.Restart.cs`, `.Fork.cs`;
`Persistence/Workflows/Commands/InsertRunCommand.cs`, `FinishRunCommand.cs`,
`PurgeRunsCommand.cs`; `WorkflowEventTypes.cs`; runtime start/child/fork admission.
Extend `src/Penghou.Zhinu.Testing/WorkflowStoreConformanceSuite.cs` with an explicitly
reported optional group-capability suite; legacy stores remain usable ungrouped.

**Invariants:** atomic step revision/membership; same-run foreign keys; conflict
on changed re-registration; stale writer cannot materialize/seal; expansion
closure refers to current boundary revisions. Crash between run creation and
manifest install cannot dispatch group-bearing work without the manifest.
Definitions and memberships are readable without replaying code. Do not infer
fan-out closure from currently persisted item rows.

**Tests/probes:** Q17-Q23, Q28-Q36, Q40-Q42; actual process termination around
manifest install, occurrence+claim commit, fan-out enumeration seal, loop commit,
group closure, selective restart and migration version bump. Extend
`tests/Penghou.Zhinu.Sqlite.ProcessProbe/Program.cs` and the existing fault-injection
decorator; an in-process reopen alone is insufficient. Verify persisted rows and
effect receipts with a fresh process before allowing recovery to execute.

**Exit:** schema-6 and upstream authority-schema-7 fixtures upgrade without loss;
schema 5 still upgrades through the existing path; old binaries reject new schema;
interrupted migrations roll
back; grouped and ungrouped recovery meet the same fencing/replay guarantees.
No automatic root/membership backfill. Include unique/foreign-key constraint and
concurrent idempotent-registration evidence.

**Dependencies:** ZG-1/2 and the authority-subject baseline
`0a523826c2d7281164ee0b1826fcb1bbee2d6689`, which landed during preparation.
The foundation uses schema 8; ZG-3b.1 uses schema 9 and preserves the nullable
authority binding and 5→6→7→8→9 migration chain. Reserve another free schema number
only if later ZG-3b work needs an additive migration. **Non-goals:**
universal migration from unsupported historical schemas, event sourcing,
group pause/cancel/retry, a second control-flow interpreter.

## ZG-4 — Coherent query and drill-down surface

**Scope:** optional engine/store group reader, root forest plus ungrouped members,
direct member/child queries, bounded progressive traversal, safe provenance and
read-only child-run links. Implement snapshot-token expiration/resync explicitly.

**Likely components:** group reader interfaces; `WorkflowEngine.cs`;
`SqliteWorkflowStore.ReadRunSnapshotCoreAsync` / `BuildSnapshotNodeAsync` patterns;
same-transaction group/approval/step queries; `RunSnapshotOptions.cs` only through
additive compatible options. Reuse `IWorkflowSnapshotRepository` conventions.
CLI integration may extend `CliApp.cs`/`CliOutput.cs`, but a UI is not required.

**Invariants:** structure and current revision facts share one boundary. When
ZG-5 adds derived state/counters, they and safe approval facts must use that same
boundary. Event-page watermark is not a pagination snapshot.
No client SQL joins or guessed key parsing. Complete summaries cannot be
calculated from a truncated page. Child trees never cross parent group ownership.

**Tests/probes:** Q37-Q42; concurrent writer during snapshot; two-page traversal
while a generation changes without a stream event; expired/lost snapshot token;
limits, cancellation, absent run/occurrence, unsupported store, deleted run and
unauthorized provenance. Check SQL plans/indexes for bounded child/member reads.

**Exit:** a generic test client reconstructs the complete supported tree by
drill-down, keeps ungrouped steps, preserves snapshot coherence and recognizes
explicit resync/limits. Source-independent inspection works after process restart.

**Dependencies:** qualified ZG-3b. **Non-goals:** ZG-5 aggregate calculations,
visual styling, web UI, exposing raw inputs,
long-lived SQLite read transactions or a persistent projection authority.

## ZG-5 — Pure aggregate state and progress

**Qualified:** [contract and closure limits](activity-groups-zg5.md),
[exact-source evidence](qualification/activity-groups/zg5.md). The implementation
uses `WorkflowGroupState.cs`, `WorkflowGroupReader.State.cs` and the optional
`IWorkflowGroupStateReader`; existing structural reader implementations remain
compatible. Active code-first/loop scopes keep unknown totals until authoritative
closure. Schema 9 and native execution algorithms are unchanged.

**Scope:** deterministic reducer implementing design section 6, Work/Control
counts, unknown totals, current versus historical occurrences, closure validity,
safe approval and existing wait reasons. Reuse `StepStatus`, do not persist
derived group status/counts in the first implementation.

**Components:** `ActivityGroups/WorkflowGroupState.cs` contains the pure reducer
and provider-neutral input/result records; `WorkflowGroupReader.State.cs` bridges
the frozen structural snapshot. Safe wait, approval and run facts reuse the
existing query DTOs.

**Invariants:** failed current control work cannot disappear behind a progress
filter; recovered old failures do not poison current status; containers/attempts
are not counted as activities; no success while expansion is open; cancellation
outside a completed group does not rewrite that group's completed history.

**Tests/probes:** Q14-Q20, Q37-Q39; table-driven complete input snapshots for every
ordered rule, sibling-order permutation invariance, nested versus flattened-leaf
equivalence, parent/child count conservation, fixed clock/expired lease cases.
Probe same three completed children with Open versus Sealed expansion and require
different completion/total results.

**Exit:** every design table row has positive and mixed-state cases; reducer does
no I/O, payload decoding, model calls or scheduling. Missing facts produce an
explicit incomplete projection. Freeze reason codes before exposing the aggregate
reader extension.

**Dependencies:** qualified ZG-3b occurrence/evidence model and ZG-4 structural
queries. Preserve the reducer's separation from I/O.
**Non-goals:** replacing run/step state machines, interpreting Fuwen item payloads,
acceptance/quality judgments, group-specific cancellation or retry behavior.

## ZG-6 — Fuwen metadata handoff and package qualification

**2026-10-19 bounded checkpoint:** [ZG-6.1 static integration](qualification/activity-groups/zg6-static.md)
is implemented and qualified against real Fuwen macro/group source. It adds the
native grouped signal-wait seam and exact candidate package handoff. The
[2026-10-20 consolidation](qualification/activity-groups/zg7.md) closes the selected
adapter profile on Windows/Linux .NET 8/10; full Q33 remains open. [ZG-6.2a](qualification/activity-groups/zg6-repeat.md) now qualifies
single-level grouped repeat lowering, native loop Control bindings and iteration
wait recovery. [ZG-6.2b](qualification/activity-groups/zg6-fanout.md) qualifies
one fixed keyed fan-out with independently durable providers and explicit item
root groups. Mixed/nested dynamic composition, coordinated inference and
item-local fallback remain rejected. Current consolidation qualifies mechanical
progress, cancellation, substantive macro vocabularies and nested macro/repeat
byte-only recovery; the broader rejected combinations remain future work.

**Scope:** consume a pinned group manifest with ordinary expanded workflow
structure; map identities and safe provenance through the existing public step
mapper; preflight exact runtime/store capabilities. This stage requires Fuwen FM
implementation; a synthetic metadata fixture alone cannot close it.

**Likely sibling components:** Fuwen `src/Penghou.Fuwen/` IR companion metadata;
`Penghou.Fuwen.Compiler/` admission/canonicalization;
`Penghou.Fuwen.Zhinu/FuwenZhinuWorkflowFactory.cs`, `FuwenZhinuStepKeys.cs`,
`FuwenZhinuStepMapper.cs`, `FuwenZhinuSequentialInterpreter.cs`,
`FuwenRepeatCoordinator.cs`, `FuwenFanOutCoordinator.cs` and adapter csproj.
Zhinu consumes generic records only; no Fuwen package enters core.

**Invariants:** preserve expanded hierarchy, exact admitted pin, per-step
authorization and existing identity; same unknown macro names work identically.
No old package is assumed to implement a new optional interface. Every declared
macro child claimed independently durable must map to its own durable boundary.

**Tests/probes:** Q43-Q47; inspect resolved NuGet closure, upgrade/legacy consumer
probe, mapper exactness against real runs, altered macro definition after start,
restart without compiler/library access, unsupported runtime rejection before
provider calls. Run two styles plus AlienProcess42.

**Required fan-out decision:** preserve old fan-out plans with their existing
item boundary. For macro-bearing per-child durable fan-out, implement and qualify
a versioned adapter lowering with new inner durable boundaries, or explicitly
reject the shape. The latter is an interim limitation and leaves Q45/full FM-ZG
completion open. Do not change semantics of historical item keys in place.

**Exit:** compatible candidate package closure and exact mapping/recovery evidence;
capability matrix truthfully lists any rejected combinations. Full stage requires
the declared supported macro subset to pass the cross-repository contract.

**Dependencies:** ZG-1/2/3/4/5 and Fuwen FM-0/1/2. **Non-goals:** redesigning
Fuwen's parser here, Hufu/provider authority changes, hidden methodology behavior,
unrelated inference hardening, publication without the normal release process.

## ZG-7 — Cross-repository qualification and handoff closure

**Scope:** execute the matrix on exact candidate commits/packages, native and
Fuwen fixtures, both target frameworks and OS CI jobs, independent process-loss
probes, API/package closure and legacy persisted-run checks.

**Likely components:** existing Zhinu tests/ProcessProbe/Testing conformance,
Fuwen adapter/compiler tests, `.github/workflows/ci.yml`, existing `eng/` package
verification scripts, new `docs/qualification/activity-groups/` evidence ledger.

**Invariants:** tests distinguish definition presence, observed step state and
semantic outcomes; no unsupported shape is silently skipped as a pass. No claims
of exactly-once remote effects beyond existing idempotency/reconciliation.

**Tests/probes:** all Q01-Q47, including external fake effect receipts before and
after process termination; independent reopened read before executing recovery;
old-style public API/binary consumer and schema migration fixtures.

**Exit:** every required case passes or is explicitly documented as blocking the
full milestone; artifacts record commit, package hashes, schema, TFM, OS, command,
exit code, assertions and logs. Update roadmap/capability docs from evidence.

**Dependencies:** all preceding stages and relevant Fuwen FM functionality.
**Non-goals:** UI delivery, new methodologies, group operational controls,
claiming publication from local packing, automatically closing unrelated gates.

## Immediate next implementation checkpoint

ZG-1, ZG-2/3a and ZG-3b.1–5 each have separate implementation and qualification
records, audited by [ZG-3b.6 consolidation](activity-groups-zg3b6.md).
[ZG-4 hierarchy queries](activity-groups-zg4.md) and
[ZG-5 aggregate state/progress](activity-groups-zg5.md) are now separately
qualified over that bounded durability contract.
[ZG-6.1](qualification/activity-groups/zg6-static.md) now qualifies real static
Fuwen expansion and exact package adoption. [ZG-6.2a](qualification/activity-groups/zg6-repeat.md)
adds independently durable single-level repeat operations, Control memberships
and native iteration waits while preserving historical macro-free keys and
claim input identity. [ZG-6.2b](qualification/activity-groups/zg6-fanout.md) now
adds bounded fan-out per-operation lowering with separate process-loss evidence
and conservative closure limits. The [current consolidation](qualification/activity-groups/zg7.md)
adds Q43–Q46 fixtures, mechanical progress and a second correctness pass. Windows/Linux Q47 evidence now closes the selected profile. Full Q33 generic
branch retirement remains open and must not be inferred from history projection.
Broader nested/mixed dynamic authoring and package publication are separate work;
the original full FM roadmap includes richer interfaces outside this subset.

ZG-0 documentation is ready when all four documents and roadmap links are
consistent. Its readiness alone does not qualify runtime behavior. ZG-1 contract
verification does not complete any ZG-2+ runtime checkbox.
