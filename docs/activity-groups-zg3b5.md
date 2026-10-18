# ZG-3b.5: native child-workflow group boundaries

## Implementation note — before coding, 2026-10-18

Verified clean `codex/zhinu-activity-groups` at
`7d230a3ebcecb5ef9bdc733fb2e2fe41e1e087ca`. The qualified ZG-3b.4 baseline has
1,571 tests, immutable explicit root selection epochs and schema 9.

Inspected `WorkflowContext.StartChildCoreAsync`, `ChildRunCoordinator`, group
binding/persistence, native child restart/cancellation/wait tests, authority
contracts, public API policy and the current roadmap/design/qualification.
Luna independently mapped child identity and store/recovery boundaries.

Native child invocation already consists of two real parent steps:
`invocation:start` admits/reuses a child and `invocation:wait` depends on that start
and reads/parks for its result. Child identity is the existing hash of parent
RunId, invocation key and start-step revision. Retry/reclaim of that revision
reuses the child; restarting start creates a new child; restarting wait alone
reuses the admitted child. Each child has its own run/initial generation and
registered manifest. Child failure/cancellation, parked waits and authority are
existing execution semantics, not new group state transitions.

The gap is that `ChildRunOptions` exposes separate StartAuthorization and
WaitAuthorization but cannot attach optional membership to those two parent
steps. The minimum runtime extension is additive `StartGroup` and `WaitGroup`
properties of type `ActivityGroupStepBinding?`, passed unchanged to the respective
existing `StepOptions.Group`. Both bindings must be prevalidated against the
parent's admitted manifest and root execution scope before either boundary claim,
so an invalid wait binding cannot leave behind a start claim or admitted child.
Each still passes ordinary exact-revision membership and generation checks at
claim. Null remains explicit absence; no synthetic or ambient group is added.

Bindings and roles are explicit. The recommended parent pair uses Control for
start and Work for wait in the same innermost parent occurrence; independent or
mixed null bindings remain supported. No binding is copied into the child. Its
own registry/manifest and explicit factories establish its separate run-local
tree. No parent occurrence pointer may cross that run boundary, even when both
manifests have identical group keys and canonical bytes.

Do not place either binding in `ChildStartRequest`, metadata, execution fingerprints
or the child ID formula. Existing serialized start inputs, step keys, APIs,
authorization declarations, dependencies, child generation binding and stores
remain unchanged. Schema 9 already stores exact parent boundary membership and
child-owned manifests/occurrences; no migration or new identity encoding is needed.
Add only the new public property accessors to the unshipped API baseline.

The selected profile is root parent invocation boundaries with explicit static
nested groups, separate child groups, reuse/retry/restart, native parent fork
reuse/reexecution, child failure/cancellation and parked/ready child waits. There
is no typed iteration StartChild API; a loop-specific occurrence passed through
the untyped WorkflowContext surface must reject, rather than invent a scope.
Mixed loop/fan-out/child lowering, Fuwen syntax and coherent child drill-through
queries remain outside this slice. Existing grouped fan-out/fork rejection stays.

Qualification will use identical parent/child manifests to make run isolation
decisive, plus mixed grouped/ungrouped and invalid binding cases. Compare parent
Control/Work memberships, child-local parent pointers, immutable history, exact
child IDs and unchanged native dependency/revision behavior. Start/wait
authorization must remain separate and protect child effects normally.

Fresh crash/recovery workers will target parent claim and child manifest
transactions, child creation before initial-generation binding, committed start,
parent wait claim, child Running/completed, parked/ready child waits and committed
parent wait. Deterministic repository barriers and callback/lease gates select
these states; sleeps cannot choose a barrier. Inspect durable parent and child
records before recovery. Recover with one consistent clock, preserve committed
work and IDs, and finish incomplete native operations under existing fencing.

Native parent forks may reuse completed child results without reparenting the
source child; parent memberships are destination-local while the existing child
reference retains native provenance. Reexecuting destination start admits its own
child using the destination RunId. This requires its own evidence and must not be
described as copying a child group tree into the destination parent.

After this selected vertical is qualified, next is ZG-3b.6 consolidated dynamic
qualification, then ZG-4 queries, ZG-5 aggregate progress, ZG-6 Fuwen and ZG-7
cross-repository qualification. Preserve the exact established finding:

**Nested repetition required no runtime algorithm, schema, or identity redesign.**

## API and ownership contract

`ChildRunOptions.StartGroup` and `WaitGroup` are independent nullable bindings.
The standard macro profile binds the existing parent `child:start` step as
Control and `child:wait` as Work in the same parent occurrence. These roles are
author choices; the runtime does not infer or override them. Either binding may
be null, and an ungrouped parent may invoke a grouped child or vice versa.

Both bindings are validated before the first start claim. Ordinary step claims
still enforce exact historical membership, including null absence. A binding
from another run, manifest or typed execution scope cannot be used as a parent
boundary. In particular, passing an iteration-specific binding through untyped
`WorkflowContext.StartChildAsync` does not establish an iteration scope.

The child receives ordinary input and native child options only. It captures its
own registration's manifest, admits its own run and generation, and establishes
its own explicit occurrences. Parent boundary membership is not serialized in
`ChildStartRequest`, added to metadata, inherited by the child, or included in
the deterministic child ID. The existing child ID is derived from parent RunId,
invocation key and start revision. Group annotation does not change it.

Reclaim/retry of an unfinished start revision reuses that child ID. Restarting
only wait keeps the same child. Restarting start uses its new revision and admits
a new child, retaining the old child and parent revision history. Native parent
fork reuse keeps the source child's provenance; it does not reparent that child
or copy its tree into the destination parent. Reexecuting start under the
destination RunId admits a destination child. Neither operation merges trees.

Parent start/wait authorization, child activity authorization, dependency edges,
lease fencing, child waits, terminal failure and cancellation keep their native
execution contracts. Group membership is durable evidence attached to those
boundaries, not an extra interpreter or cancellation state machine.

## Scope limits

This is the native root invocation profile. Typed loop/fan-out child lowering,
Fuwen child syntax, cross-run hierarchy/drill-through queries, aggregate state
and progress, and generalized grouped fan-out/fork copying are not qualified by
this slice. Native cancellation of a run subtree remains the existing sequence
of separately durable operations; it is not made an atomic subtree transaction.
No schema migration, occurrence hash/version or child identity mechanism is added.
