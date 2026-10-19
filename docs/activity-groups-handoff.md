# Activity groups: continuation handoff

Updated 2026-10-20. No chat history is required.

## Checkout and checkpoint

The user resumed implementation after the earlier usage pause. The current
boundary is **ZG-7 selected-profile consolidation and second review**; read its
[current qualification](qualification/activity-groups/zg7.md),
[second pass](qualification/activity-groups/second-pass-review.md), then the original
[fan-out qualification](qualification/activity-groups/zg6-fanout.md), the
[repeat checkpoint](qualification/activity-groups/zg6-repeat.md), the earlier
[static integration record](qualification/activity-groups/zg6-static.md), then
ZG-5
[contract](activity-groups-zg5.md) and
[qualification record](qualification/activity-groups/zg5.md), followed by the
[pre-Fuwen review](qualification/activity-groups/pre-fuwen-review.md) and
[measured follow-up](qualification/activity-groups/pre-fuwen-followup.md).
The selected profile is qualified on Windows/Linux .NET 8/10. Next full-matrix
work is **Q33 generic controlling-branch replacement/current-selection retirement**;
existing conservative history projection is not proof of that behavior. The local
full suites, exact package consumers and both legacy probes
pass for native preview.9 (`246d535`) and Fuwen preview.18 (`535f82e`). CI-only
harness commits do not change those frozen runtime sources. Broader rejected
DSL combinations and package publication are separate future work. Each prior
profile retains separate evidence and limits.
Previously admitted unfinished selection work remains an obligation; this does
not implement in-place replanning, implicit retirement or a first-class epoch API.
Preserve exact-source evidence and rerun checks when subsequent changes warrant
them rather than restarting completed qualification.

- Repository: `https://github.com/jenolaszlo-sketch/penghou-zhinu.git`.
- Branch: `codex/zhinu-activity-groups`.
- Worktree: `C:/Users/Laszlos/source/repos/Solo/workstreams/zhinu-activity-groups`.
- ZG-1/base: `d2821b20a3ae767aa24627cd345f1880416829bc` (from `b7f1009`).
- Foundation implementation commit: `130e927f2d08f803e69a2e5f4c437e4e1bbacb30`.
- Qualified foundation/evidence commit: `33c051ed4907178e13d273e930d1f63342321f3f`.
- Current dynamic boundary: [ZG-3b.6 consolidation](activity-groups-zg3b6.md) and
  [evidence/source commits](qualification/activity-groups/zg3b6.md). The commits
  above remain the exact foundation baseline, not the current runtime head.
- Pre-Fuwen follow-up source: `7a5f87637ac68c877b4e2f933b829d67f3703c4c`.
  Shares prepared state facts through snapshot-owned, cache-budgeted indexes;
  verifies closure vector digests and fixes Unicode fan-out snapshot ordering.
  Local projection measurement for 1,024 item groups plus root: 259 ms / 573 MiB
  allocated becomes 1.04 ms / 2.49 MiB. The follow-up record retains the exact
  measurement scope and final qualification evidence: **1,897 tests**, formatting,
  seven packages and both consumer/binary/schema probes passed. This remains the
  exact earlier pre-Fuwen baseline.
- Original pre-Fuwen review source: `5bd107b857b16cc810530756ba5d212e32930c4e`.
  Fixes stale approval projection, silently omitted damaged membership and
  incomplete provider snapshot validation; indexes immutable manifest definitions.
  Full suite: **1,877 tests**, including 32 new regression executions; formatting,
  seven packages and both consumer/binary/schema probes passed. The review
  record identifies final package evidence and retained pre-fix failures.
  No public API, schema, identity or durable execution-transition changes.
- Original ZG-5 qualified source/packages: `01f2ac64173a625f3bf745704391871aac014a72`.
  Feature implementation: `e8bb89c975f32695364b7836e6ba41856e681094`.
  ZG-5 adds a pure aggregate reducer and optional state reader over the same
  frozen ZG-4 facts and timestamp. All **1,845 tests** pass (96 new executions),
  plus formatting, seven unpublished packages and both isolated package and
  legacy binary/schema probes. The ledger retains 50 query/projection receipts
  and 14 matching packaged/build binaries. Active code-first/loop groups keep
  unknown totals until authoritative closure; historical selection exposes
  explicit unknown/partial coverage. No aggregate state is persisted.
  Schema 9, occurrence encoding and execution/authority algorithms are unchanged.
  The final source also hardens only the shared hard-loss test helper, keeping
  its exact fault boundaries; earlier worker-exit timeouts remain in evidence.
- ZG-4 qualified reader/runtime/package source: `f911630e52743373ced9f5be0e8d33e1142e63b2`.
  ZG-4 adds safe coherent hierarchy snapshots, bounded immutable pagination,
  explicit resync outcomes and host read policy. All **1,749 tests** pass in the
  clean final full run, including **102 new query executions**; formatting,
  seven packages and isolated package/binary/schema probes pass on .NET 8/10.
  The evidence retains 36 query receipts and two new fresh-process inspectors.
  Schema 9 and identity/scheduling/admission algorithms remain unchanged.
- ZG-3b.1 runtime/package source: `e4b7f139b93c3934860ed5a3f7aae9225d3f5ba3`;
  1,441 tests, formatting and both package/binary probes passed on .NET 8/10.
  Qualification/evidence commit: `1d3c232d6c19b6a618a5a5feeb55cd3cd0b7e2fe`.
- ZG-3b.2 runtime/package source: `28818cfc12831978a06b2fcd46d258699f0b9204`;
  1,499 tests, fourteen fork recovery receipts, formatting and both package/binary
  probes passed on .NET 8/10. The following evidence commit changes docs only.
- ZG-3b.3 source: `4d24d6c4e25efc4efdf99b4e3f44f461e227019d`, starting from
  `ef6d2fe3bf8d7490d5e6e89fbd5406f457f85c7f`. Fixed native 3×2 repetition,
  full typed lineage and immediate parents, activity retries, selective restart
  and fourteen new process-loss cases (28 fresh crash/recovery worker starts).
  All 1,531 tests, formatting and both package/binary probes passed on .NET 8/10;
  qualification retains the exact source/package hashes and recovery receipts.
  No runtime algorithm, identity encoding or schema change was needed.
- ZG-3b.4 source: `bfa1bf6c71b5bf4a29fe09a508f9a237b0f4cea6`, starting from
  `9be3308555e2fe2c4cc169391a4baf6cc32a8d91`. Explicit pinned epoch schedule,
  distinct stable root fan-out/selection keys and immutable historical membership.
  Expand/contract/reorder/disappear/reappear, retry/restart and changed current
  selectors are qualified under that profile. Eight positive crash/recovery
  boundaries per framework plus a separate omission rejection case retain raw
  receipts. All **1,571 tests**, formatting, seven package builds and both isolated
  package/binary/schema probes passed on .NET 8/10. Evidence retains 16 positive
  recovery receipts and 20 semantic receipts (including two omission rejection
  process cases), with **36 fresh worker starts** overall.
  No first-class epoch API, runtime algorithm, schema or occurrence codec change.
- ZG-3b.5 source: `3ca11057c5c43463bc9c82304058d68e60b3433e`, starting from
  `7d230a3ebcecb5ef9bdc733fb2e2fe41e1e087ca`. Optional parent start/wait bindings
  and independent child trees are qualified through native retry/restart, fork
  provenance, authorization, cancellation and process loss. All **1,639 tests**,
  full formatting verification, seven packages and both isolated package/binary
  probes passed on .NET 8/10. The record retains 16 recovery receipts, 48 semantic
  receipts and **38 fresh worker starts**, covering ten distinct crash boundaries
  per framework. Schema 9, child IDs, serialized start requests and occurrence
  encoders are unchanged. Typed loop/fan-out child lowering remains open.
- ZG-3b.6 changes tests/tooling/docs only; runtime/package source remains the
  exact ZG-3b.5 source above. Its audit checks all five source ledgers, 55 TRX
  files and 35 historical candidate packages, plus eight new focused guard tests.
  The retained complete suite has 1,639 tests; the eight additional executions
  are separate targeted coverage, not a new full-suite count. Historical ledgers
  contain 68 positive recovery receipts, two omission rejection cases and 146
  tranche-specific worker starts; consolidation starts no process-loss workers.
  Explicit rejection is distinguished from unqualified lexical composition.
- SQLite schema: **9**, additive migrations **5→6→7→8→9**.
- Current candidate: `0.2.0-preview.5`, exact source
  `fd1875886e67e3aacd9c03c5689238788575d1e5`; earlier preview.4 evidence retains
  its original source. Local packages are not publication evidence.
- Qualification: [ZG-2/3a evidence](qualification/activity-groups/zg2-3a.md) and its JSON record.

Each profile's qualification record identifies its exact runtime/package source;
the following qualification commit adds evidence/docs only. Use
`git rev-parse HEAD` for the checkout's current documentation HEAD. Do not work in
the canonical main checkout or disturb its unrelated `docs/ideas.md` edit.

## Read first

1. [Measured pre-Fuwen follow-up](qualification/activity-groups/pre-fuwen-followup.md) and
   [original review](qualification/activity-groups/pre-fuwen-review.md), then
   [ZG-5 aggregate contract and limits](activity-groups-zg5.md) and
   [qualification](qualification/activity-groups/zg5.md), then
   [ZG-4 query contract](activity-groups-zg4.md) and
   [qualification](qualification/activity-groups/zg4.md), then
   [ZG-3b.6 native capability boundary](activity-groups-zg3b6.md),
   [ZG-3b.5 native child profile and pre-code note](activity-groups-zg3b5.md),
   [ZG-3b.4 supported explicit-epoch profile and pre-code note](activity-groups-zg3b4.md),
   [ZG-3b.3 supported nested profile and pre-code note](activity-groups-zg3b3.md),
   [ZG-3b.2 supported fork profile](activity-groups-zg3b2.md),
   [ZG-3b.1 supported fan-out profile](activity-groups-zg3b1.md),
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

Use each profile's explicit supported/remaining lists. The foundation qualified
one native loop under a root logical parent; ZG-3b.3 separately qualifies fixed
nested native loops with three outer and two inner iterations, exactly six leaf
occurrences and reconstructable immediate-parent ancestry. This is not proof of
arbitrary dynamic expansion or aggregate progress. ZG-3b.4 separately qualifies
evolving sets across explicit immutable root epochs, without rewriting old scopes.

- Grouped forks use `IWorkflowGroupForkRepository` to atomically install the
  target manifest and rebind copied parent/member lineage under the existing
  destination RunId, preserving native root/loop coordinates and reuse selection.
  Null membership stays null; incompatible copied groups abort. A grouped fan-out
  source remains a rejected combined profile until expansion evidence can be
  rebound coherently. Every FanOutAsync overload supplied direct Group still
  rejects. Additive FanOutGroup supports all four native
  overloads at root scope with an explicit current completed selection step and
  declared dependency. Ungrouped fan-out remains ordinary.
- Schema 9 durably admits the full selected set before item claims; keyed tokens
  and tagged positional indices have v2 coordinates, preserving root/loop v1.
  Append-only exact-revision closure proofs become non-current on restart.
  Never derive item identities from arbitrary dotted step keys.
- ZG-3b.3 preserves full outer-first typed-loop coordinates and explicit parents
  through inner/outer activity retry, selective restart and early/later process
  loss. Native commits/finals govern completion. Condition/input changes affecting
  selection, broader partial expansion and skipped branches remain open.
- ZG-3b.4 pins epoch identities and ordered selector output in ordinary durable
  steps, with distinct native fan-out keys per epoch. Completed historical
  selectors replay despite today's changed decisions; only unselected future
  epochs consult current selectors. Older unfinished scopes remain obligations,
  including when a newer epoch excludes their items. Omitting an incomplete scope
  fails the exact existing completion guard. Same-scope changed plans still reject;
  no implicit retirement/cancellation or active-only progress model is added.
- `ChildRunOptions.StartGroup` and `WaitGroup` independently attach membership to
  native parent start/wait steps, with both bindings validated before start.
  Children admit their own manifest and run-local group tree. Native child IDs,
  reuse/restart/fork provenance, authorization, cancellation and interrupted
  execution/wait recovery retain their existing semantics. Typed iteration and
  fan-out child lowering remain outside the root invocation profile.
- ZG-4 qualifies hierarchy/tree queries and a bounded reader-local snapshot cache.
  Older point reads remain diagnostic capabilities without a multi-call snapshot
  guarantee. ZG-5 adds aggregate state/progress over that immutable snapshot with
  explicit extent/coverage and separate Work/Control counts. It does not invent
  closure for active arbitrary code-first/loop groups or reinterpret retained
  historical fan-out selections as current work. The Fuwen adapter is qualified
  separately in ZG-7; group controls, UI and broader DSL dynamics remain outside
  the selected profile.
- Local Windows .NET qualification is not Linux/remote CI or package publication.

## Next checkpoint

The selected static/repeat/fan-out Fuwen profile and Windows/Linux package gates
are complete. Use [ZG-7](qualification/activity-groups/zg7.md) and its
[second review](qualification/activity-groups/second-pass-review.md) as the current
checkpoint; the original preview.5/.7/.8 records retain their own source and limits.

The full-matrix follow-up is **Q33 generic controlling-branch replacement and
current-selection retirement**. Existing superseded fan-out history is excluded
from current progress, and unresolved parents expose Partial/Unknown coverage.
That is not evidence that an arbitrary changed branch can retire old pending
membership. Establish the explicit supported selection/retirement contract and
its durable evidence before claiming that broader behavior; unfinished admitted
work must not disappear by inference from a later selector result.

Nested/mixed DSL dynamics, evolving-selection lowering, item waits/checkpoints/
merges, grouped coordinated inference/fallback and group controls remain separate
work. Native forks, fixed nested loops, explicit selection epochs and native
child boundaries retain their independent qualified profiles. No broader
fan-out-aware fork or arbitrary mixed composition is inferred from them.
**Nested repetition required no runtime algorithm, schema, or identity redesign.**

The completed selected-profile sequence is ZG-3b → ZG-4 queries → ZG-5 aggregate
state/progress → ZG-6 Fuwen adapter → ZG-7 cross-repository qualification.
Keep the runtime/source/CI-harness commit distinction and exact archive pins;
publication and downstream application adoption require their own gates.

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
Grouped forks do not need a new schema: all pointers are destination-local and
normal grouped claims can resume them. Preserve the superseding-revision
membership clone for native changed-input fork reuse. Source grants are never
copied; a reexecuted protected operation is evaluated under current authority.

Do not redesign control flow, step keys, child IDs, operation IDs, generation
ownership, authorization grants, compensation semantics, Hufu boundaries or
historical Fuwen fingerprints. No group callback, lease, output, retry policy or
persisted autonomous state machine should be introduced. Manifest limits and
canonical encodings are compatibility contracts. Keep provenance bounded and
safe; do not copy macro arguments or executable Fuwen IR into group storage.


## ZG-6.2b exact-source checkpoint

Native runtime/package source `fa0297b9dc21b235f27ac92b979dcf9faeb6b7b6`,
unpublished preview.8, qualifies independently durable keyed item bodies, typed
claim/closure fencing and dependent restart. Schema9 and existing v2 coordinates
remain unchanged. Native regression: 1,925; Fuwen companion: 1,690 plus 12
package-only executions. See the new [ledger](qualification/activity-groups/zg6-fanout.json).

One fan-out scope, no repeat composition, explicit item root, providers and
control-only conditionals are the bounded Fuwen contract. Broader compositions,
evolving selection, item waits/checkpoints and full progress/closure integration
are deferred. `StepOnly` body/item-completion restart is explicitly rejected.
Use the current Fuwen preview.16 native pin, not the preserved preview.7 repeat
or preview.5 static pins. Packages are not published.

Use `D:/temp/activity-groups/tmp` for TEMP/TMP. Generated artifacts and all
src/tests bin/obj directories are on D: through workspace junctions. Preserve
these paths and exact package archives; C: previously exhausted during copying.
