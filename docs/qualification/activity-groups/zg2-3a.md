# ZG-2 + ZG-3a qualification

Date: 2026-10-16. Result: **passed locally on Windows, .NET 8 and .NET 10**.
Branch: `codex/zhinu-activity-groups`.
Base/ZG-1: `d2821b20a3ae767aa24627cd345f1880416829bc`.
Runtime implementation and package source: `130e927f2d08f803e69a2e5f4c437e4e1bbacb30`.
The following qualification commit adds the corrected package-probe assertion,
this evidence and the continuation documentation; it changes no runtime C#.
Exact source/package hashes, resolved dependencies and probe records: [zg2-3a.json](zg2-3a.json).
Contract: [foundation](../../activity-groups-zg2-3a.md).
Continuation: [handoff](../../activity-groups-handoff.md).

## Results

| Check | Result |
| --- | --- |
| Full Release solution tests | 1,339 passed, 0 failed, 0 skipped, 11 assemblies/TFMs |
| Core | 126 per framework, including 15 new occurrence/admission/scope/vector cases per framework |
| SQLite | 498 per framework, including 24 new group cases per framework |
| Repository formatting/analyzers | Passed; 0 of 910 files changed by final verification |
| Pack | Seven package/symbol pairs at existing source version 0.2.0-preview.4 |
| Isolated package closure | Passed on net8.0/net10.0; fresh cache, exact local candidates, no project references, no Hufu dependency |
| Grouped package consumer | Two authorized native-loop body activities, distinct occurrences under a persisted parent, exact revision membership restored through a new store |
| Legacy compiled consumer | Published 0.1.0-preview.15 consumer runs against candidate assemblies without recompilation on both frameworks |
| Retained database | Published schema-5 rows upgraded to 8, retained 2 prior step rows plus 2 candidate rows on each framework |
| Old worker | Rejected schema 8 clearly on both frameworks; schema and retained rows remained unchanged |
| v7 migration | Retained legacy run/step/authority, null membership, repeated initialization and a successful grouped claim after migration |
| Process loss | Before/after loop commit plus hard exit inside both manifest and membership transactions, on both frameworks |
| Independent review | Luna source maps, tests, and read-only review; reported defects fixed; final scope/replay audit clear |

The pinned neutral contract remains `Penghou.Workflow.Abstractions` 0.1.0-preview.2.
No dependencies or package versions were changed for groups. Packing rebuilt the
qualified runtime commit; final source hashes identify the qualification script
correction separately from package provenance.

## SUPPORTED

- Definitions remain separate from deterministic run-local occurrences. Identity
  excludes display/provenance, manifest digest, attempts, revisions and leases;
  independent byte-level vectors freeze occurrence v1.
- Explicit root/typed-loop occurrence factories, nested same-run parents and
  innermost Work/Control membership. Root ancestors may span loop iterations;
  sibling/future iteration bindings reject before a step claim.
- Mixed grouped/ungrouped work, immutable exact-revision membership, retry/expired
  lease reclaim retaining the occurrence, and selective-restart storage inheritance.
- Optional manifest-provider/store admission, including custom registrations and
  registries, Start/Handle/Run, terminal resume and restart/rollback preflight.
  Missing, removed, changed or inconsistent group capability/manifest fails closed.
- A child registration admits its own run-local manifest or fails before child
  creation/callback. This establishes admission, not the broader child recovery matrix.
- Atomic run/manifest/start-event and step/ancestor/membership/claim-event writes
  under existing generation/lease fences; exact step-row foreign keys, bounded
  canonical restoration, immutable pins/tuples and run retention cascade.
- One real repeated case: a native loop creates two child-group occurrences under
  a common logical root. A separate process dies before or after the second loop
  commit; recovery retains byte-identical manifest, parent/occurrence coordinates
  and exact revision membership, returns `2`, and repeats no body operation.
- A separate SQLite connection decorator installs a hard-exit function and trigger.
  Exit 93 during manifest insertion leaves no run/manifest/start event. Exit 93
  during membership insertion rolls back the body step and both ancestor rows,
  retains the previously admitted manifest, then resumes successfully after the
  test trigger is removed. These are real process losses inside transactions.
- Existing ungrouped, authority, control-flow, generation, recovery, event/snapshot,
  artifact, retention and legacy capability regressions remain green.

## NOT YET QUALIFIED

- Grouped native fan-out and grouped forks are explicitly rejected before affected
  work. All four FanOutAsync overloads reject supplied Group options; no item scope
  or destination occurrence rebind is silently fabricated.
- Complete nested repeated loops, selection/enumeration/closure, skipped branches,
  control-input restart invalidation and partial dynamic expansion recovery.
- Parent child-invocation start/wait membership, child reuse/restart/fork/cancellation
  and broader operation recovery. Native child admission is not Fuwen child syntax.
- Hierarchy/tree queries, aggregate status/progress, open/closed expansion evidence,
  UI, Fuwen adapter/DSL runtime and cross-repository integration. Point persistence
  reads do not establish any of these capabilities or a multi-call snapshot.
- Linux, remote CI, NuGet publication or mixed-version workers.

The full Q01-Q47 matrix remains open. These cases qualify foundation identity,
admission and persistence aspects only; they do not close query/progress or all
dynamic requirements that share those case identifiers.

## Review corrections and limits

Independent review found missing preflight before restart/rollback mutation,
absent-to-present membership drift on an existing revision, and a composite
foreign-key index missing from migrated step tables. All were corrected and
covered by tests. Scope validation additionally prevents a saved iteration
occurrence from leaking to another iteration. Loop StepOptions copies now preserve
existing authority declarations as well as group/retry/dependency settings.

The initial full-run child assertion queried an earlier successful child's shared
database; it now filters the failed parent's ID. The first package group probe
incorrectly counted empty-requirement loop-control authority evaluations as body
declarations; the corrected probe asserts exactly two nonempty `execute`
declarations and the expected result. The passing records refer to the corrected
assertions. No runtime authority behavior was weakened to satisfy the probe.
One silent formatter verification was stopped and rerun with diagnostics and
build-server reuse disabled; the completed final check passed with zero changes.

## Reproduce

From the worktree root with the installed .NET 8/10 runtimes and repository SDK:

```powershell
dotnet restore Penghou.Zhinu.slnx
dotnet test Penghou.Zhinu.slnx -c Release --no-restore --logger trx --results-directory artifacts/zg2-qualified-final-tests
dotnet format Penghou.Zhinu.slnx --verify-no-changes --no-restore
dotnet pack Penghou.Zhinu.slnx -c Release --no-restore -o artifacts/zg2-final-packages
./eng/Verify-WorkflowPackageClosure.ps1 -ArtifactsDirectory artifacts/zg2-final-packages -PackageVersion 0.2.0-preview.4 -OutputDirectory artifacts/zg2-passed-closure
./eng/Verify-LegacyWorkflowConsumer.ps1 -ArtifactsDirectory artifacts/zg2-final-packages -PackageVersion 0.2.0-preview.4 -ExpectedCandidateSchemaVersion 8 -OutputDirectory artifacts/zg2-final-legacy
```

Use a fresh results/output directory on reruns. TRX, detailed formatter/build/probe
logs, candidate packages and generated consumers remain under ignored
`artifacts/zg2-*`. The checked-in JSON retains the aggregate records, exact hashes
and resolved package/binary evidence needed to identify this checkpoint.
