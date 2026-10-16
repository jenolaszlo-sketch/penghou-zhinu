# Activity groups: continuation handoff

Prepared 2026-10-16. No chat history is required.

## Checkout and checkpoint

- Repository: `https://github.com/jenolaszlo-sketch/penghou-zhinu.git`.
- Branch: `codex/zhinu-activity-groups`.
- Worktree: `C:/Users/Laszlos/source/repos/Solo/workstreams/zhinu-activity-groups`.
- ZG-1/base: `d2821b20a3ae767aa24627cd345f1880416829bc` (from `b7f1009`).
- Implementation commit: `130e927f2d08f803e69a2e5f4c437e4e1bbacb30`.
- Qualified checkpoint/evidence commit: `33c051ed4907178e13d273e930d1f63342321f3f`.
- Current dynamic slice: [ZG-3b.1 contract](activity-groups-zg3b1.md) and
  [evidence/source commits](qualification/activity-groups/zg3b1.md). The commits
  above remain the exact foundation baseline, not the current runtime head.
- SQLite schema: **9**, additive migrations **5→6→7→8→9**.
- Package source candidate stays `0.2.0-preview.4`; local packages are not publication evidence.
- Qualification: [ZG-2/3a evidence](qualification/activity-groups/zg2-3a.md) and its JSON record.

The implementation commit identifies the qualified runtime source exactly. A
following qualification commit records evidence, the package probe assertion
correction and this handoff; use
`git rev-parse HEAD` for the checkout's current documentation HEAD. Do not work in
the canonical main checkout or disturb its unrelated `docs/ideas.md` edit.

## Read first

1. [ZG-3b.1 supported native profile](activity-groups-zg3b1.md),
   [ZG-3b remaining scope and gates](activity-groups-zg3b.md), then
   [foundation contract and bytes](activity-groups-zg2-3a.md).
2. [Qualification and reproduction](qualification/activity-groups/zg2-3a.md).
3. [Design](activity-groups-design.md), [stage plan](activity-groups-implementation-plan.md),
   [full qualification matrix](activity-groups-qualification.md), [roadmap](../ROADMAP.md).
4. [Provider durability contract](store-contract.md), [public API policy](public-api-policy.md).
5. `src/Penghou.Zhinu/ActivityGroups/`, `WorkflowContext.cs`, `WorkflowLoopIteration.cs`,
   `WorkflowEngine.cs`, `Context/ChildRunCoordinator.cs`, `Execution/Steps/RunExecutionPipeline.cs`.
6. `src/Penghou.Zhinu.Sqlite/Persistence/Groups/SqliteWorkflowGroupRepository.cs`,
   `SqliteDatabase.cs`, `Persistence/Steps/SqliteStepRepository.Claims.cs`, `.Restart.cs`, `.Fork.cs`.
7. New `ActivityGroup*Tests.cs` in core/SQLite tests and the existing separate ProcessProbe.

## What is established

ZG-1 freezes immutable bounded Logical/Macro definitions, safe opaque macro
provenance, optional Work/Control membership, forest validation and manifest v1
canonical bytes/digest vectors. The original engine overloads and legacy
capability slots remain present. See [ZG-1](activity-groups-zg1.md).

ZG-2/3a adds explicit immutable occurrence references, root/typed-loop factories,
optional registration/store capabilities, an immutable run manifest pin, atomic
run/manifest admission, atomic exact-revision membership, selective-restart
inheritance and real process-loss reconstruction. Missing/changed registration,
manifest or store capability fails before grouped execution or restart/rollback
mutation. Custom registries go through the same admission helper. Child runs
admit their own manifest; no parent occurrence crosses a run boundary.

Occurrence v1 is SHA-256 of the domain prefix and length-delimited RunId,
GroupKey, explicit parent occurrence ID and canonical outermost-first existing
loop coordinates. It excludes display/macro metadata, manifest digest, Attempt,
Revision, leases and execution generation. The manifest digest separately pins
all immutable annotation bytes; edits conflict for an admitted run. Do not put
annotation metadata into DefinitionFingerprint or mutable MetadataJson.

StepOptions.Group explicitly binds membership to an occurrence. Loop wrappers
carry the actual typed iteration for scope validation and preserve authorization,
compensation authorization, retry, timeout and dependencies. An ancestor/root
occurrence can span iterations; an iteration-specific binding cannot leak into a
sibling iteration. Use iteration.StepAsync for loop members. Null membership is
first-class, including ordinary steps and control steps inside grouped runs.
There is no AsyncLocal group scope and no implicit synthetic root.

Factories return references; the first member claim materializes required
ancestors. Every grouped-run claim carries its manifest pin. Existing revisions
compare exact membership including null before reuse/retry/wait; they cannot
acquire it late. Retry keeps the same row/occurrence. Selective restart copies
membership to the new revision and preserves history. Existing generation and
lease checks precede writes. All annotation records cascade on run deletion.

## Supported versus still open

Use the evidence's explicit SUPPORTED/NOT YET QUALIFIED lists. The qualified
repeated example is one native loop with two occurrences of a child group under
a root logical parent. Static nested groups, mixed membership, retry/revision
storage and process loss are foundation support, not proof of complete dynamic
expansion or aggregate progress.

- Grouped forks and every native FanOutAsync overload supplied direct Group
  reject before affected work. Additive FanOutGroup supports all four native
  overloads at root scope with an explicit current completed selection step and
  declared dependency. Ungrouped fan-out remains ordinary.
- Schema 9 durably admits the full selected set before item claims; keyed tokens
  and tagged positional indices have v2 coordinates, preserving root/loop v1.
  Append-only exact-revision closure proofs become non-current on restart.
  Never derive item identities from arbitrary dotted step keys.
- Full nested-loop repeated groups, condition/input restart affecting expansion,
  partial expansion and skipped branches need dedicated ZG-3 qualification.
- Parent child-invocation start/wait membership is not exposed by ChildRunOptions.
  Child-owned manifest admission is tested; broader child restart/reuse/fork,
  cancellation and boundary-group operation recovery remain open.
- No hierarchy/tree queries, aggregate state/progress, group controls, cache,
  UI, Fuwen macro adapter or DSL runtime support is claimed. Point reads are
  bounded diagnostic persistence reads, not a coherent multi-call tree snapshot.
- Local Windows .NET qualification is not Linux/remote CI or package publication.

## Next checkpoint

Continue [ZG-3b dynamic durability](activity-groups-zg3b.md) with **3b.2 grouped
forks**, then 3b.3 nested repeated occurrences/parents, 3b.4 broader partial/evolving
dynamic expansion and recovery, 3b.5 child-workflow boundaries and 3b.6 consolidated
qualification. ZG-3b.1's fixed-selection fan-out and four hard-exit proofs are
qualified separately; they do not close the entire dynamic model.
Preserve explicit rejection until each complete durable vertical passes its own
real-process and compatibility gates. Static/restart correctness does not imply
fan-out/fork correctness; retain separate evidence for every dynamic construct.

The user-directed sequence is ZG-3b → ZG-4 hierarchy/query projection → ZG-5
aggregate state/progress → ZG-6 Fuwen handoff/integration → ZG-7 cross-repository
qualification. This supersedes the earlier reducer-before-query stage ordering.
Queries/progress must build on the qualified dynamic occurrence model, and Fuwen
adoption must wait for the structures its selected profile can emit. Extend the
established architecture rather than revisiting its fundamental contracts.

## Checks and known traps

Run the full solution Release suite on both frameworks, formatting verification,
all seven package builds and both eng package probes. Keep authorization, legacy
capability, fencing, loop/fan-out/child, restart/rollback, event/snapshot and
retention tests green. The group process probes kill before/after a loop commit
and inside manifest/claim SQLite transactions; an in-process reopen is insufficient.

The schema's exact-revision foreign key needs the explicit composite unique
index on old workflow_steps tables as well as fresh databases. Migration tests
must claim grouped work after upgrade, not only check the version stamp. Preserve
authority migration's intermediate literal schema 7/schema 8 and the full 5→6→7→8→9 chain.
Older worker rejection and retained rows are qualified by the binary package probe.
The probe's ExpectedCandidateSchemaVersion is now 9. Preserve the monotonic
GroupFanOutUsed trigger on fresh databases as well as migrations. Closure reads
must evaluate current revisions coherently, and a new item revision needs a new
proof instead of trying to replace immutable historical closure.

Do not redesign control flow, step keys, child IDs, operation IDs, generation
ownership, authorization grants, compensation semantics, Hufu boundaries or
historical Fuwen fingerprints. No group callback, lease, output, retry policy or
persisted autonomous state machine should be introduced. Manifest limits and
canonical encodings are compatibility contracts. Keep provenance bounded and
safe; do not copy macro arguments or executable Fuwen IR into group storage.
