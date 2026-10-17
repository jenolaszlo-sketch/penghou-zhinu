# Durable activity groups: implementation design

Date: 2026-10-16. Status: ZG-1 contracts qualified; the
[ZG-2/3a foundation](activity-groups-zg2-3a.md) is implemented with explicit
[qualification boundaries](qualification/activity-groups/zg2-3a.md). ZG-1 freezes
manifest encoding/limits; ZG-2/3a freezes occurrence encoding and durable admission.
Read with the [implementation plan](activity-groups-implementation-plan.md),
[qualification matrix](activity-groups-qualification.md) and
[next-agent handoff](activity-groups-handoff.md). Decisions below are the proposed
implementation baseline, derived from local source inspection, not shipped APIs.

## 1. Boundary and decisions

**Activity groups annotate execution structure; they do not define execution
structure.** The existing workflow/control-flow graph determines what runs when.
Fuwen determines expansion; Zhinu preserves structural intent and durable facts.

1. A run has an optional forest of group definitions and occurrences. Ungrouped
   steps remain valid. There is no mandatory persisted root or activity parent.
2. An executing step belongs directly to at most one innermost group occurrence.
   Ancestors are reached through the group tree, never copied memberships.
3. Logical and Macro differ only in provenance. Unknown macro names are ordinary
   metadata; no methodology registry, dispatch switch or authorization follows.
4. Distinct static definitions and dynamic occurrences are necessary. Reuse the
   existing run, step key, revision, loop and fan-out coordinates. Do not derive
   group identity from display text, random IDs, lease generation or retry count.
5. Membership attaches to durable **step revisions**, not activity descriptors.
   There is no universal persisted `Activity` entity to add a parent field to.
6. Reuse `StepStatus` for projected lifecycle. Add group diagnostic reasons and
   completeness/expansion fields; do not add Blocked to persisted step status.
7. Persist structure, occurrence membership and necessary expansion evidence;
   derive counts and status from a coherent read. Events notify; they are not a
   replacement replay engine or sole source of the hierarchy.
8. Recovery reads the pinned group manifest. It does not recompile Fuwen macros,
   inspect a live library or execute a workflow merely to show its hierarchy.
9. Group controls, UI implementation, new orchestration semantics and arbitrary
   dynamic code/graph generation are outside this implementation.

## 2. Current architecture, with source anchors

Paths below are relative to the Zhinu repository unless explicitly marked Fuwen.
The table describes the inspected committed baseline; concurrent working-tree
changes and their reconciliation are recorded immediately below it.

| Concern | Actual source / behavior | Reuse or extension |
| --- | --- | --- |
| Workflow definition | `src/Penghou.Zhinu/WorkflowDefinition.cs` contains Name/Version only. `WorkflowRegistration.cs` discovers optional `IWorkflowFingerprint`; `WorkflowRegistry.cs` keys registrations by name/version. | Optional manifest-provider capability at registration; do not turn the core definition into a second Fuwen IR. |
| Declarative activity definitions | `Declarative/ActivityDescriptor.cs`, `ActivityReference.cs`, `CompiledWorkflowDefinition.cs` describe callable contracts and compiled steps. Functional and class-based workflows also exist. | Groups describe invocations, not reusable callable descriptors. Preserve legacy declarative serialization/fingerprints. |
| Run and generations | `WorkflowRun.cs`: run Guid, optional DefinitionFingerprint, ParentRunId, SourceRunId, LeaseGeneration. `WorkflowInstance.cs`: separate instance and execution-generation records. | Bind the manifest to one run and its admitted definition/generation. LeaseGeneration is a write fence, not group identity. |
| Durable execution | `WorkflowStepRun.cs`: Guid Id, WorkflowRunId, StepKey, Revision, Attempt, status. `StepClaimRequest.cs` and `Persistence/Steps/SqliteStepRepository.Claims.cs` create/claim rows transactionally. | Add optional membership evidence in the same claim transaction, including reuse paths. |
| Retry/restart | Retry claims the same revision and advances Attempt; selective restart inserts a new Revision. `SqliteStepRepository.Restart.cs` retains history and invalidates dependents. | Same occurrence across attempts/revisions; membership and expansion evidence remain revision-aware. |
| Loops | `DurableLoopIdentity.cs`, `WorkflowLoopReference.cs`, `WorkflowLoopIteration.cs`, `WorkflowContext.Loops.cs`. Durable condition/body/commit/limits/final steps encode one-based iteration identity; nesting already has typed parent-iteration references. | Reuse those coordinates and final/commit boundaries. These parent relationships are control-flow ownership, not a missing activity presentation tree. |
| Native fan-out | `WorkflowContext.cs` keyed `FanOutAsync` validates all keys then schedules bounded chunks of ordinary steps using `prefix.key`; positional overloads use `prefix.index`. | Reuse item identity. No persisted complete manifest or general item-body hierarchy exists; add bounded annotation membership evidence for grouped fan-out. Never parse arbitrary dotted keys to guess scopes. |
| Children | `WorkflowContext.StartChildCoreAsync` creates `key:start` and `key:wait`; `Context/ChildRunCoordinator.cs` derives child ID from parent run, invocation key and start-step revision. | Both parent boundary steps can belong to a parent group; child run keeps its own tree. |
| Persistence | `IWorkflowStore.cs` composes repository interfaces. SQLite delegates to repositories and SQL command/query classes. `SqliteDatabase.cs` owns initialization and transactional schema migration. | Optional group capability and additive tables; no breaking required method on every third-party store. |
| Status and waits | `StepStatus.cs`: Pending, Running, Completed, Failed, Waiting, Cancelled. `WorkflowStatus.cs` also has Compensated/RollingBack. `WorkflowWait.cs` has Signal/Retry/Child/Delay. `WorkflowAuthorization.cs` holds separate approval-pending facts. | Project existing facts. Approval is not a new signal kind; compensation is not rewritten forward success. |
| Read models | `WorkflowRunProgress.cs`, `WorkflowLoopProgress.cs`, `RunDiagnosis.cs`; atomic `WorkflowRunSnapshot.cs` and `IWorkflowSnapshotRepository.cs`. `SqliteWorkflowStore.BuildSnapshotNodeAsync` reads current revisions and durable watermark on one connection/transaction. | Use the existing snapshot implementation pattern. Current snapshots omit pending authorization rows and group expansion data; add bounded safe facts for group projection. |
| Events | `WorkflowEventTypes.cs`, `WorkflowEventDurability.cs`, `WorkflowEventPage.cs`, `WorkflowEventExport.cs`. Ordered durable/advisory classification, pages and durable consumer cursors exist. | Add bounded durable group-change notifications to the existing log. Keep established generation/operation snapshot-only policy. |
| Operator graph | `src/Penghou.Zhinu.Cli/CliApp.cs` and `CliOutput.cs`: `runs graph` combines recorded steps, waits, dependencies. | Extend an inspection surface, not a second scheduler. Its current separate reads are not an atomic group snapshot. |
| Compatibility | `ZhinuSqliteSchema.CurrentVersion` is 6. `SqliteDatabase.InitializeAsync` accepts 5 and 6; migrates 5 to 6 atomically. `WorkflowAuthorizationMigrationTests` proves rollback/retained rows. | Design the next additive migration; do not assume the stale exception comment saying previews never migrate is authoritative. |
| Fuwen mapping | Sibling Fuwen `FuwenZhinuStepKeys.cs` and public `FuwenZhinuStepMapper.cs` already map declared/synthetic steps and loop/item scopes. | Extend this map; operator clients must not invent step-key parsers. |

The initially inspected Zhinu commit was `99d9d78e863badb45571f1f3799d79a6fb88c39f`, a
0.2.0-preview.3 **candidate**, not evidence of publication. Its M5 snapshot,
watermark and event-page APIs already exist. The older roadmap's M5 missing-seam
assessment is historical for this checkout.

**Concurrent-work reconciliation:** during final preparation, separate
uncommitted authority-subject work appeared in the same checkout. It adds
`WorkflowRun.AuthoritySubjectBinding`, changes start/fork plumbing and public
signatures, and raises the working-tree SQLite schema from 6 to **7**, with a
6-to-7 migration. The map above records the inspected committed baseline; these
later edits are not group implementation and were neither changed nor qualified
by this task. Preserve the opaque authority binding in all future group
start/fork integration. It is not a group ID, manifest fingerprint or grant.
Before finalization, that work landed as
`0a523826c2d7281164ee0b1826fcb1bbee2d6689` (AE-6.2). This is the final source
baseline beneath the documentation commits. Schema 7 is now committed; the
observed file inventory in the handoff is historical, not remaining dirty work.
Rebaseline against it and any newer commits before coding.

### Material adapter limits discovered

Fuwen commit `603b2e78d3f3ff33e2152641ab0ca016a120552e` declares version
0.1.0-preview.13 but references **Zhinu 0.1.0-preview.14** in
`src/Penghou.Fuwen.Zhinu/Penghou.Fuwen.Zhinu.csproj`. New source in a sibling
checkout does not change that package dependency. Qualify a compatible package
closure before claiming any cross-repository support.

In `FuwenZhinuSequentialInterpreter.ExecuteFanOutAsync`, one durable item step
covers the entire item body. `ExecuteFanOutActivityAsync` and context/inference
helpers call providers directly inside that boundary. `CoveredDeclaredNodePaths`
in the public mapper explicitly records this many-to-one mapping. Item failure
can also be encoded in a **Completed** item outcome and subsequently fail the
aggregate workflow. Zhinu must not infer domain failure by decoding Fuwen payloads.

Consequently, per-activity durable macro semantics inside a Fuwen fan-out body
are **not** obtained by adding groups. Full FM/ZG fan-out qualification requires
a separately versioned adapter extension with durable inner steps and ordinary
failure semantics, or the macro-bearing fan-out form must fail preflight. Merely
rejecting it permits a bounded interim release, not completion of the full matrix.
Do not fabricate independently completed children from one completed item row.
Nested repeat/fan-out and child-workflow syntax are not generally supported by
the current adapter; native Zhinu tests and adapter support claims stay distinct.

## 3. Minimum model and storage responsibilities

Names in this section are design names; final additive API naming follows
[API conventions](api-conventions.md).

```text
GroupManifest (optional, immutable, versioned)
  ContractVersion, ManifestDigest, workflow/plan binding
  Definitions[]
    GroupKey, ParentGroupKey?, Kind(Logical|Macro), DisplayName
    ChildOrdinal, Provenance?

GroupOccurrence (durable, run-local)
  RunId, ManifestDigest, GroupOccurrenceId, GroupKey
  ParentGroupOccurrenceId?, Coordinates[], SiblingOrder

GroupStepMembership (durable, optional)
  RunId, StepId, StepKey, StepRevision, GroupOccurrenceId
  MemberRole(Work|Control), MemberOrder

GroupExpansionEvidence (durable annotation where not derivable)
  RunId, GroupOccurrenceId, EvidenceId
  ProducerBoundaryReferences[], KnownMembers[{StepKey, Role, Order}]
  KnownChildOccurrenceIds[]
  Closure(Open|Sealed), ClosureReason, ProducerFence
```

No `ParentActivityId`. No group has a retry policy, execution callback, lease,
output, capability grant or autonomous state machine. `MemberRole` changes only
progress counting, not execution or whether failure is visible. Source mapping
to one optional logical display activity can later coalesce multiple control
steps; initial queries expose the exact step keys and their roles honestly.

The normalized SQLite shape is a run-bound manifest/definition table, occurrence
table, step-membership table and expansion-evidence table. A manifest may be
stored once as bounded canonical bytes with indexed group definitions; avoid
storing executable Fuwen IR a second time. Use run-cascade retention. Composite
foreign keys enforce same-run ownership; unique constraints enforce definition
keys, occurrence identity and at-most-one membership per step revision. Validate
the acyclic parent graph before transaction commit. A group definition forest
can exist even when some branches never produce an occurrence.

Known member declarations describe future or existing ordinary durable steps; they do
not insert Pending execution rows, add dependency edges or schedule callbacks.
Absent membership means null/ungrouped. An ungrouped step remains at the run
root in a query. A workflow with no manifest returns an empty group collection,
not fabricated groups. Distinguish that from an unsupported group reader.

Definitions, provenance and membership survive without a live workflow registry.
Execution still requires the registered compatible workflow as it does today.
Do not use mutable `WorkflowRun.MetadataJson` as the manifest: its documented
contract excludes idempotency/definition validation and permits updates.

## 4. Definition and occurrence identity

`GroupKey` is a stable invocation-local structural key supplied by Fuwen or a
code-first author, unique within the manifest. The same macro invoked twice has
two keys even with identical arguments. Nested calls have their own keys; macro
name/fingerprint never substitutes for invocation identity.

The semantic occurrence identity is the tuple:

```text
(occurrence-v1, RunId, GroupKey, ParentOccurrenceId?, enclosing Coordinates[])
```

The manifest digest pins immutable annotation bytes separately and is excluded
from occurrence identity: changing display/provenance must not change the
identity of the same run/key/parent/coordinate tuple. Such a manifest change is
still rejected for an already admitted run. This supersedes the initial tuple
that included ManifestDigest.

Coordinates are ordered **outermost first**. The foundation implements existing
typed loop frames. [ZG-3b.1](activity-groups-zg3b1.md) adds one explicitly versioned
root native fan-out frame. ZG-3b.3 qualifies fixed nested native loops using the
unchanged outer-first typed frames; mixed fan-out nesting and Fuwen tokens remain targets:

- Loop: stable loop control identity plus one-based iteration number, using
  `WorkflowLoopReference`/`WorkflowLoopIterationReference` where applicable.
- Fan-out: stable fan-out control identity plus exact canonical item key token.
  Native string keys are ordinal; preserve Fuwen's typed `RuntimeIdentityKey`
  token rather than converting distinct typed keys to the same display string.
- Positional fan-out: explicitly tagged index only for the existing positional
  profile; never infer positional identity for keyed fan-out.

Use a versioned, length-delimited UTF-8 encoding and full SHA-256 hex ID for new
group occurrence keys; retain/compare the tuple on idempotent insertion so a
collision or corrupted identity fails closed. Guid run IDs use canonical `D`
format, numeric coordinates invariant decimal, strings ordinal without silent
case/Unicode normalization. Define byte-level golden vectors before exposing
the encoder. No change to existing step-key or child-run encoders is required.

`ManifestDigest` binds all immutable grouping metadata, including display and
safe provenance. It is an annotation digest, not an execution fingerprint. Fuwen
must bind it alongside the exact plan/admission without reinterpreting historical
execution fingerprints. Metadata-only edits affect a newly registered manifest,
not a running instance. No mutable relabel/reparent API is in this slice.

Parent occurrence is explicit and must match the declared parent group and the
appropriate prefix of enclosing coordinates. A logical parent outside a loop
can contain iteration-specific child occurrences. It does not acquire the
child's iteration coordinate itself. The compiler/adapter supplies scope
ownership; Zhinu checks consistency, not methodology. Child order is declared
ordinal, then iteration/source-item order, then an ordinal identity tie-breaker;
order is not identity or scheduling.

Examples (readable tuple notation, not storage encoding):

```text
delivery                 []
delivery.coding          [loop(reviewCycle,1)]
delivery.coding          [loop(reviewCycle,2)]
delivery.qualification   [fanout(repositories,string:repo-A)]
delivery.qualification   [fanout(repositories,string:repo-B)]
```

Retry changes Attempt only: same occurrence and membership row. Selective
restart changes StepId/Revision: append a membership for the new revision, keep
old history, and calculate current state from current revisions. Lease rollover
never creates an occurrence. A new run has its own run-bound occurrences;
execution-generation changes within the same run do not change occurrence IDs.
ZG-3b.2 now implements this for native separate-run forks; grouped fan-out/fork
combinations remain rejected. Fork support must create
destination memberships and never foreign references to source groups. Preserve
normal source/producer provenance.

When restart changes a controlling condition/input revision, old expansion
evidence is historical. Effective occurrences/members are those reachable from
the current expansion evidence; previously visited but now unselected branches
remain inspectable as historical, not counted as current pending work. A normal
step retry must not invalidate an unrelated branch or every group in the run.

## 5. Expansion and unknown totals

Group nesting alone cannot prove total work. Native loops have committed
iteration/final boundaries; ungrouped native fan-out has no durable enumeration-complete
record. ZG-3b.1 opts into bounded immutable selection and current-revision closure
at an explicit existing selection step. Ordinary code-first control flow is arbitrary C#. Therefore the runtime
cannot infer completeness merely by scanning keys or observing a disposed scope.

For grouped execution, the existing orchestrator/adapter supplies bounded
annotation evidence of selected member keys and child occurrences. Each evidence
record references the exact durable control/input revisions that caused it.
Register a complete selected fan-out key list before dispatching its first
grouped item. Large bounded lists may be chunked, but only a committed terminal
enumeration marker closes the list. Crash after a prefix means Open, never a
smaller successful fan-out. This annotation records the selection already made
by the orchestrator; it does not choose items or decide when to execute them.

A group inside one loop iteration can close its own membership after that
iteration's selection is fixed. An outer group containing the loop remains open
until the loop's durable final boundary or an explicit terminal interruption.
All branch selectors and iteration boundaries used as evidence must reference
current revisions. Reuse existing loop commits/final steps; do not add an
executable "complete group" activity just to make a UI green.

For code-first dynamic control with no durable selection boundary, exact
pre-completion totals cannot be promised. It may expose conservative Open
coverage, or opt into the generic evidence API using durable boundary references.
The run's successful terminal commit can supply final closure for materialized
groups, atomically with that commit. A later restart to Pending invalidates that
run-terminal closure. Failed/cancelled runs stop expansion as interrupted, not
successful. An annotation-only lexical scope must never seal in a `finally` or
`Dispose` path that also runs after failure/cancellation/process loss.

Closure evidence is valid only while its referenced revisions remain current
and meet their recorded durable condition. Restart invalidates affected evidence
in the same transaction, or the reader excludes it by revision join immediately;
do not leave a window of stale Completed projection. Claiming a grouped member
requires matching current evidence/occurrence. All evidence writes use existing
run ownership, lease and execution-generation checks. A stale worker cannot add
children or seal an old occurrence after restart/cutover.

Persist the minimal evidence that cannot be reconstructed; derive the following:

```text
KnownCount             distinct selected Work member keys in current subtree
CompletedCount         those with current StepStatus.Completed
Failed/Waiting/...     counts using current revisions only
IsExpansionClosed     current selection sealed for entire subtree
TotalCount             KnownCount only if closure and coverage are complete;
                       otherwise null
Coverage               Complete | Partial | Unknown
```

Control helpers and group containers do not add Work counts. They still
participate in status/failure and drill-down. Child-run steps are counted only
in their owning run. Retries and historical revisions never inflate progress.
`3 completed / 3 known / expansion open / total unknown` is a valid projection.
Even sealed membership may still contain pending work: closed does not mean done.

## 6. Deterministic state projection

Use a read-only `GroupState` projection with `StepStatus? Status`, reason codes,
`IsBlocked`, counts, active keys, coverage and closure. Null status is reserved
for missing/inconsistent required data, never silently Completed. The display
word Blocked means Pending with `IsBlocked=true`; it is not a seventh step state.
Approval, child, delay and retry waits use Waiting plus a specific reason.

Evaluate the following ordered rules over effective current descendant member
revisions, control boundaries and child occurrences in **one read transaction**:

| Order | Condition | Result |
| --- | --- | --- |
| 1 | Required manifest/evidence invalid, unsupported, truncated or missing | Explicit incomplete/error result; nullable status, no success claim. A paged response may still include a complete separately calculated summary. |
| 2 | Any current terminal Failed descendant or unresolved failed control boundary | Failed, even if other children run; retain all active/waiting counts. |
| 3 | Any current Running descendant | Running; expired lease adds AwaitingRecovery, not synthetic failure or success. |
| 4 | Any current parked wait, unresolved approval, or Waiting descendant | Waiting; choose stable reason ordering Approval, Signal, Child, Retry, Delay, Other and return all reasons. Ready approval/wait uses AwaitingDispatch until authoritative step transition. |
| 5 | Expansion closed normally, coverage complete, all selected Work and Control members completed; child occurrences satisfy same rule | Completed. A deliberately entered, sealed empty group completes; no vacuous success for an open empty group. |
| 6 | Remaining subtree interrupted by authoritative cancellation or supersession, or has Cancelled members and no active/waiting work | Cancelled. Mixed Completed/Cancelled is Cancelled, with both counts visible. A fully completed group stays Completed when unrelated run work is cancelled later. |
| 7 | No active/waiting/failure, but current work is prevented by known unmet dependencies, quiescence, or a failed run outside this subtree | Pending, IsBlocked=true with exact reason. Do not relabel unchanged step rows. |
| 8 | Otherwise: all pending, not yet entered, or known children complete but expansion still open | Pending, reason NotStarted or ExpansionOpen. No manufactured 100% completion. |

Nested aggregation is computed over leaf/control facts plus expansion state,
not by assigning arbitrary precedence to cached child display labels. A nested
failure thus reaches its ancestors; containers are not counted a second time.
For a known pending member with no row, project Pending from its declaration.
Definitions on an unselected branch have no runtime occurrence and display as
NotEntered definition metadata, not a completed or cancelled runtime group.

Transient errors parked for retry do not trigger rule 2. A failed old revision
followed by a new successful current revision can recover to Completed; retain
failure history separately. Never infer recovery from `Error == null` alone.
If workflow policy handles a terminal failed step through a separate fallback
without revising that failed step, the mechanical group projection remains Failed
and may coexist with a Completed run. Acceptance/handled-outcome metadata belongs
to the existing workflow/adapter contract, not a group-specific success override.
Group status never prevents the workflow from following its existing policy.
An unhandled run failure outside a group leaves completed groups completed and
unfinished groups blocked; report the owning run's failure alongside them.

There is no initial group-cancel command. Run cancellation uses existing facts;
a future group command must settle ordinary work through authorized runtime
controls before its projection changes. RollingBack/Compensated and generation
Quiescing/Superseded appear as run/generation context, not new group execution
states or rewritten history. Keep forward results and compensation outcomes
separate. Scalar status is deliberately lossy; counts/reasons preserve mixed work.

## 7. Child workflows

Attach both existing `invocation:start` and `invocation:wait` boundary steps to
the caller's innermost group. They retain their normal dependency and respective
authorization. The start is Control, the wait is Work for initial count purposes;
either can fail the group. Link the child using the existing child ID/wait facts.

The child has a different RunId, its own manifest and group occurrence namespace.
No parent occurrence pointer crosses the run boundary. A drill-through query
returns a child-run reference, not child members merged into the parent's tree.
Restart of the start-step revision creates the existing new child identity;
old child evidence stays historical. Fuwen child-workflow syntax remains a
separate deferred feature; qualify this boundary first through native Zhinu.

## 8. Provenance and validation

Durable required Macro provenance: bounded MacroName, opaque exact
MacroDefinitionFingerprint and InvocationKey. Optional: explicit MacroVersion,
sanitized relative source location or source artifact reference, and an
allowlisted display label. Logical groups reject macro provenance. Fuwen
produces and verifies definition identity; Zhinu validates presence, bounds,
format/version and correlation with the manifest, without resolving macro code.

No arbitrary argument JSON, prompt/specification text, credentials, model
transcripts or repository contents belong in these fields. Even a raw hash of
a low-entropy secret can disclose it: argument fingerprints are not required
and require the host's disclosure policy. Source locations may be sensitive;
prefer relative/source-artifact references and existing authorized inspection.

Reject duplicate keys, unknown parents, containment cycles, inconsistent parent
occurrences, invalid coordinates, cross-run membership, conflicting immutable
re-registration and unsupported kinds/contracts. Establish explicit manifest
byte, text, group count, nesting, occurrence, member and page budgets. Nesting
has a configurable resource ceiling, not a hard two-level product restriction.
Validate iteratively to avoid stack exhaustion. Exact numerical defaults are a
ZG-1 benchmark/API decision; bounded behavior is mandatory from the first slice.

## 9. Persistence, events and compatibility

Use optional group store/reader capabilities, following the authorization and
snapshot precedent. A group-aware registration fails before callbacks if the
configured store cannot preserve its contract. Legacy registrations never need
the new capability. Do not silently flatten a group-bearing plan.

Manifest installation is idempotent and bound before the first grouped claim;
new-start/child/fork paths must not dispatch until installation is complete.
Persist an explicit installation state or include it in start admission so a
crash between run creation and installation is resumable and cannot lose intent.
Same-run conflicting re-registration fails. Inserting a step revision and its
membership is atomic, including restarted/copied revisions. Existing completed
step reuse validates membership rather than attaching new claims after the fact.

Foundation manifest/claim writes share the existing WorkflowStarted/StepStarted
transactions and event log; no separate group event stream was introduced.
Later expansion changes will need bounded invalidation events (batch where practical).
Idempotent annotation insertion does not invent duplicate group events. These events
are invalidation/correlation hints; a client refreshes the group snapshot. No
per-group derived-status events or second event log are needed. Preserve existing
per-run cursor/export semantics and the snapshot-only generation/operation policy.

The initial baseline was schema 6; AE-6.2 added authority schema 7.
The foundation uses **schema 8** with additive transactional 7-to-8 migration;
ZG-3b.1 adds **schema 9** with immutable native fan-out evidence and a monotonic
run capability marker,
preserving 5-to-6 and 6-to-7 so supported older databases upgrade through the
chain. Do not reuse or overwrite the authority migration.
Empty group tables represent old runs; do not
backfill artificial roots or memberships. Preserve IDs, step revisions/results,
events/cursors, authorizations, waits, artifacts and definition fingerprints.
Failed migration must leave both rows and marker at the old version. Old workers
must reject the new group schema; no mixed-writer downgrade. Back up before upgrade;
restore backup to downgrade. Older unsupported/unversioned databases retain the
existing fail-closed policy, not a new promise of universal migration.

Public additions use optional properties/overloads/capability interfaces, not
new required members on existing third-party implementations. Historical
serialized definitions omit groups and retain their canonical bytes/digests.
Do not add null/default fields to an old canonical serializer. Group metadata
uses a new versioned companion manifest bound to admission; it cannot authorize
reuse, change existing operation IDs, or disguise changed executable semantics.
Old-style definitions on new code remain group-free. Existing runs are not
restamped with newly discovered metadata during recovery.

## 10. Query contract for operator clients

Follow `Get` for a bounded materialized lookup, `Query` for filtered/paged
collections. Proposed optional reader methods: `GetRunGroupSnapshotAsync`,
`GetGroupOccurrenceAsync`, `QueryGroupChildrenAsync` and
`QueryGroupMembersAsync`. Final names/signatures are ZG-4 API work, not current
methods. Do not add a full traversal API to every `WorkflowHandle` by default.

The run-root result includes group roots, ungrouped steps and child-run links.
Each node exposes key/occurrence identity, parent, kind, safe display/provenance,
state/progress, has-children and completeness. Definition-only/unentered groups
are distinguishable from runtime occurrences. Preserve stable ordering and
include all current revision data needed by the reducer without payload reads.

Extend the existing same-connection snapshot implementation pattern. Read group
rows, effective expansion references, current member steps, waits, safe pending
approval facts, dependencies, run/generation and durable watermark atomically.
Do not join `GetPendingAuthorizationAsync` or a sequence lookup after the read
transaction and call it coherent. Approval summaries expose only safe identity
and pending/ready facts, not authorization context payloads.

Pagination returns `NextCursor`, `HasMore`, coverage and the read boundary.
Do not equate an event watermark with an immutable pagination snapshot: existing
generation/operation changes can be snapshot-only. For the first implementation,
materialize a bounded immutable server-side snapshot for paginated traversal,
with an opaque token and expiry. Losing/expiring the token returns ResyncRequired;
it is a read cache, not durable authority. The client restarts from a new coherent
snapshot. Enforce memory/TTL limits; never retain a database read transaction
across user requests. A snapshot above the configured cap returns an explicit
limit/incomplete result rather than claiming complete totals. Every accepted
bounded manifest must be queryable under a compatible configured read budget.

No styling, icon choices, screen layout or methodology-specific labels enter
Zhinu. Marang may collapse/expand and drill through child-run links using this
semantic surface alone.

## 11. Remaining implementation decisions and stop conditions

Resolved here: optional forest, direct membership, definition/occurrence split,
identity inputs, retry/restart behavior, child isolation, status precedence,
unknown totals, sidecar evidence requirement, schema direction and reader model.

Manifest v1 encoding/digest vectors and resource defaults are frozen in ZG-1.
The ZG-2/3a foundation freezes explicit runtime factories, occurrence v1 bytes,
optional provider/store capabilities and schema 8. ZG-3b.1 freezes root native
fan-out coordinates, fixed-selection admission/closure and schema 9. ZG-3b.3
qualifies fixed nested loops without changing those contracts. Snapshot-cache
limits, mixed dynamic scopes and evolving selection remain later decisions.
These are bounded engineering decisions,
not permission to redesign execution semantics.

Fuwen separately owns grammar, parameter/result contracts and its versioned
admission binding. Package closure and fan-out per-node durability are explicit
integration gates. If either is unresolved, Zhinu native work may continue;
FM/ZG end-to-end completion must remain unchecked. Do not make new claims of
hard inference guarantees, artifact-reuse authority or generalized child/loop
syntax while implementing structural groups.
