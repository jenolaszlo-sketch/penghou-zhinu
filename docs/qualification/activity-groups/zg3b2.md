# ZG-3b.2 qualification: grouped native forks

Date: 2026-10-17. Status: **passed for the standalone native fork profile**.
Base: `1d3c232d6c19b6a618a5a5feeb55cd3cd0b7e2fe` on
`codex/zhinu-activity-groups`.
Runtime/package source: `28818cfc12831978a06b2fcd46d258699f0b9204`.
The commit containing this record adds evidence/docs only. Exact test receipts,
source/package hashes, fourteen process recovery records and compatibility results
are retained in [zg3b2.json](zg3b2.json). Read the
[supported contract](../../activity-groups-zg3b2.md) before applying this evidence.

| Gate | Result |
| --- | --- |
| Release tests | **1,499 passed**, zero failures/skips across 11 assembly/framework combinations on .NET 8/10 |
| Formatting | Full solution verify-no-changes passed |
| Process loss | Seven barriers on each framework; fourteen before/after receipts retained |
| Packages | All seven unpublished `0.2.0-preview.4` candidates built from the exact runtime source commit |
| Package-only consumer | Passed both frameworks, including protected grouped-loop forks with destination-owned copied parents/members |
| Legacy binary | Published `0.1.0-preview.15` consumer unchanged while candidate runtime assemblies were substituted; passed both frameworks |
| Schema/old worker | Existing schema remains 9; old worker rejected it without changing version or four retained step rows |
| Review | Independent Luna source review found no remaining runtime issue; final documentation audit reconciled evidence/status/date |

The full test run completed on 2026-10-16; final formatting, package and binary
gates completed on 2026-10-17. The schema source, occurrence encoder, manifest
encoder and mandatory fork interface are byte-for-byte unchanged from the base;
their hashes are included alongside the changed source files. The genuine retained
schema-8→9 grouped-row migration test is separate from the published binary probe's
schema-5→9 migration. This is Windows evidence, without Linux/remote CI claims.

## Independent fork claim

A native fork owns a new RunId, a separate target manifest, destination-local
occurrence ancestry and exact copied membership. The existing root/loop coordinate
bytes and occurrence hash domains are unchanged. Copied and reexecuted steps keep
the native fork reuse/authority contract; sibling groups never alias or merge.
This is a separate-run fork, not a new in-run branching primitive.

Supported proofs include:

- Two sibling destination runs reuse the same definitions with distinct group
  identities and correct nested parents; source records remain unchanged.
- An ordinary join does not inherit or merge grouped dependency identities.
- A running branch is selectively restarted with stale execution fenced, while
  its completed sibling work and exact membership remain reusable.
- Retry inside a fork retains occurrence and exact revision membership.
- Native cross-version changed-input supersede preserves inherited occurrence,
  revision history and old membership; compatible target presentation may differ.
- Mixed group/null membership and ordinary-source forks to grouped/ungrouped
  targets preserve copied absence and allow newly declared grouped work.
- Missing optional capability, incompatible target pins/parents/kinds/provenance,
  malformed source occurrences, conflicting destinations and authority changes
  reject without partial destination records.
- Protected reuse copies no authorization evidence; reexecution evaluates current
  authority and a denied callback produces no protected effect.
- Retention of the source cannot remove the destination's rebound group tree.
- Genuine schema-8→9 migration retains source grouped/ordinary rows, manifest and
  authority, then successfully forks them. Existing migration regressions remain.

## Real process barriers

Every worker exits with code **93**. A fresh store captures source, completed
sibling and interrupted destination before callbacks. After recovery, compare
manifests, RunIds/SourceRunIds, coordinates/IDs/parents, step IDs/revisions/attempts,
memberships, event records and callback receipts. The evidence JSON retains the
actual before/after records for every framework/mode.

| Mode | Barrier | Durable destination before recovery | Recovery callbacks |
| --- | --- | --- | --- |
| manifest | During destination manifest insertion | No destination rows | work + join |
| membership | During copied membership insertion | No destination rows, including previously inserted ancestry/step | work + join |
| event | Before fork event insertion/transaction commit | No destination rows | work + join |
| committed | After fork transaction commit, before first-generation binding | Pending run, copied seed/plain, manifest/ancestry; zero execution generations | work + join |
| running | Inside work callback after its claim/membership commit | Running work in destination occurrence | unfinished work + join |
| member-completed | After work result commit, before join | Completed work reused | join only |
| joined | After join result commit, before run completion | All results committed | none |

No recovery callback reruns copied seed/plain or any completed member. Source
and completed sibling records stay identical. After committed creation, repeating
ForkAsync with the known destination ID preserves native conflict semantics;
recovery executes the existing run instead of creating another branch.

## Reproduction

```powershell
dotnet format Penghou.Zhinu.slnx --verify-no-changes --no-restore
dotnet test Penghou.Zhinu.slnx -c Release --no-restore --logger trx --results-directory artifacts/zg3b2-qualified-tests
dotnet pack Penghou.Zhinu.slnx -c Release --no-restore -o artifacts/zg3b2-packages
./eng/Verify-WorkflowPackageClosure.ps1 -ArtifactsDirectory artifacts/zg3b2-packages -PackageVersion 0.2.0-preview.4 -OutputDirectory artifacts/zg3b2-closure
./eng/Verify-LegacyWorkflowConsumer.ps1 -ArtifactsDirectory artifacts/zg3b2-packages -PackageVersion 0.2.0-preview.4 -OutputDirectory artifacts/zg3b2-legacy
```

Focused suites: `ActivityGroupForkTests`, `ActivityGroupForkStoreTests`,
`ActivityGroupForkRecoveryTests`. The process worker and linked definition are
`GroupForkProbe.cs` and `GroupForkWorkflow.cs` in the existing ProcessProbe project.

## Still open

Grouped fan-out/fork evidence rebinding remains explicitly rejected. Nested
repeated group composability is **3b.3 next**; evolving selection stays in 3b.4,
child boundaries in 3b.5, then consolidated dynamic qualification. No hierarchy
query, aggregate progress, Fuwen integration, Linux/remote qualification or package
publication is claimed. Existing schema remains **9** and original APIs remain.
