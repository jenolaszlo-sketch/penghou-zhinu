# Activity groups: continuation handoff

Prepared 2026-10-16. No chat history is required.

## Checkout and checkpoint

- Repository: `https://github.com/jenolaszlo-sketch/penghou-zhinu.git`.
- Branch: `codex/zhinu-activity-groups`.
- Worktree: `C:/Users/Laszlos/source/repos/Solo/workstreams/zhinu-activity-groups`.
- ZG-1/base: `d2821b20a3ae767aa24627cd345f1880416829bc` (from `b7f1009`).
- Implementation commit: `130e927f2d08f803e69a2e5f4c437e4e1bbacb30`.
- SQLite schema: **8**, additive migrations **5→6→7→8**.
- Package source candidate stays `0.2.0-preview.4`; local packages are not publication evidence.
- Qualification: [ZG-2/3a evidence](qualification/activity-groups/zg2-3a.md) and its JSON record.

The implementation commit identifies the qualified runtime source exactly. A
following qualification commit records evidence, the package probe assertion
correction and this handoff; use
`git rev-parse HEAD` for the checkout's current documentation HEAD. Do not work in
the canonical main checkout or disturb its unrelated `docs/ideas.md` edit.

## Read first

1. [Foundation contract and bytes](activity-groups-zg2-3a.md).
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

- Grouped forks and every native FanOutAsync overload supplied Group reject
  before affected work. Ungrouped fan-out inside a grouped run remains ordinary.
- No durable keyed/typed/positional item-scope or enumeration/selection closure
  exists. Never derive item identities from arbitrary dotted step keys.
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

Continue the remaining ZG-3 dynamic durability slice: freeze a typed native item
coordinate only alongside durable enumeration/selection and current-revision
closure evidence; prove partial expansion across process loss and stale writers.
Extend nested-loop, child-boundary and fork destination rebind coverage. Preserve
explicit rejection until each path is durable. Keep ZG-3 open, then implement the
ZG-5 pure reducer before freezing ZG-4 read DTOs; ZG-6/7 Fuwen adoption follows
exact package qualification. Do not infer Fuwen support from native tests.

## Checks and known traps

Run the full solution Release suite on both frameworks, formatting verification,
all seven package builds and both eng package probes. Keep authorization, legacy
capability, fencing, loop/fan-out/child, restart/rollback, event/snapshot and
retention tests green. The group process probes kill before/after a loop commit
and inside manifest/claim SQLite transactions; an in-process reopen is insufficient.

The schema's exact-revision foreign key needs the explicit composite unique
index on old workflow_steps tables as well as fresh databases. Migration tests
must claim grouped work after upgrade, not only check the version stamp. Preserve
authority migration's intermediate literal schema 7 and the full 5→6→7→8 chain.
Older worker rejection and retained rows are qualified by the binary package probe.
The probe's ExpectedCandidateSchemaVersion is now 8.

Do not redesign control flow, step keys, child IDs, operation IDs, generation
ownership, authorization grants, compensation semantics, Hufu boundaries or
historical Fuwen fingerprints. No group callback, lease, output, retry policy or
persisted autonomous state machine should be introduced. Manifest limits and
canonical encodings are compatibility contracts. Keep provenance bounded and
safe; do not copy macro arguments or executable Fuwen IR into group storage.
