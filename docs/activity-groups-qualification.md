# Activity groups: qualification matrix

Date: 2026-10-18. Status: **ZG-1, ZG-2/3a, root ZG-3b.1 fan-out, ZG-3b.2 forks, ZG-3b.3 fixed nested repetition, ZG-3b.4 explicit immutable selection epochs and ZG-3b.5 native child boundaries qualified separately;
the full runtime matrix remains open**.
The [ZG-1 evidence](qualification/activity-groups/zg1.md) now records passing
contract-level cases. The [ZG-2/3a evidence](qualification/activity-groups/zg2-3a.md)
records the narrow root/loop durability, admission and process-loss slice.
The [ZG-3b.1 evidence](qualification/activity-groups/zg3b1.md) adds root native
keyed/positional fan-out admission, exact-revision closure and process recovery.
Native portions of Q12/Q13/Q23/Q27/Q31/Q32/Q34/Q35 are covered only within that
fixed-selection profile. The [ZG-3b.2 evidence](qualification/activity-groups/zg3b2.md)
covers Q36's native separate-run fork ownership and relevant Q18/Q27/Q31/Q32/Q34/Q35
restart, fencing, recovery and migration cases. The
[ZG-3b.3 evidence](qualification/activity-groups/zg3b3.md) covers native Q11 and
relevant Q10/Q18/Q27/Q31/Q32 durability within fixed 3×2 nested loops: six leaf
occurrences, complete typed lineage, explicit parents, retries, selective restart
and seven process-loss boundaries on each framework. This does not close arbitrary
mixed composition, grouped fan-out/fork evidence, implicit selection retirement,
aggregate summaries or group queries.
The [ZG-3b.4 evidence](qualification/activity-groups/zg3b4.md) separately qualifies
changing sets across distinct stable root scopes, replayed historical selection,
unmodified old occurrences/memberships and unfinished obligations. This covers
the explicit-epoch native portions of Q12/Q13/Q23/Q31 only; prior retry, fencing
and restart cases remain regression gates. In-place
old-plan replacement, active/history reducers and public projections remain open.
The [ZG-3b.5 evidence](qualification/activity-groups/zg3b5.md) covers native Q42
boundary membership and run-local child ownership, plus relevant Q18/Q27/Q29/Q30/Q31/Q32
retry/restart, admission, execution and parked/ready child-wait recovery. Separate
start/wait/child authorization, successful parent cancellation and native fork
provenance remain ordinary runtime semantics. Typed child lowering, coherent
cross-run queries and aggregate progress are not closed by those proofs.
Consolidation must prominently retain: **Nested repetition required no runtime
algorithm, schema, or identity redesign.**
Use the [design](activity-groups-design.md) and
[stage plan](activity-groups-implementation-plan.md). Native Zhinu and Fuwen
adapter profiles are separate: native support does not prove DSL support.

Each case records before/after IDs, current and historical revisions, hierarchy,
provenance, statuses/reasons/counts, expansion coverage, event cursor and fake
effect receipts. Verify both returned API models and durable records. All
rows describe required behaviors. The profile evidence above identifies the
implemented suites and their exact coverage; unchecked broader cases remain work.

## Concrete cases

| ID | Scenario / stimulus | Required observable result | Stage |
| --- | --- | --- | --- |
| Q01 | Standalone ordinary activity, no manifest | Null membership, zero groups, unchanged result/key/fingerprint. | 1,3 |
| Q02 | Existing ungrouped workflow, both new run and resumed old run | No synthetic root/backfill; identical execution behavior. | 1,3 |
| Q03 | One Logical group, one Macro group, mixed grouped/ungrouped steps | Separate kinds, direct membership once, ungrouped steps visible at run root. | 1,3 |
| Q04 | Logical→Logical, Logical→Macro, Macro→Logical, Macro→Macro | Correct parent and innermost member queries for all four combinations. | 1,4 |
| Q05 | At least four levels, then exceed configured depth/bytes/member budgets | Valid deeper tree works; oversized input fails boundedly without partial rows/stack exhaustion. | 1,3 |
| Q06 | Same macro twice, same/different parameters | Distinct invocation/group keys, same definition pin where appropriate; no argument payload leakage. | 1,6 |
| Q07 | Same nested macro under different parents; reorder unrelated siblings | Different parents remain distinct; stable occurrence identity under order-only changes within a fixed manifest contract test. Manifest changes themselves produce their own new digest. | 1,2 |
| Q08 | Duplicate key, missing parent, cycle, cross-run parent/member, wrong kind/provenance | Deterministic rejection before callback; no orphan membership. | 1,3 |
| Q09 | Caller mutates input lists/metadata after registration; conflicting idempotent registration | Frozen bytes unchanged; conflicts rejected; exact retry creates no duplicate event. | 1,3 |
| Q10 | Same macro group in loop iterations 1 and 2 | Separate occurrences; typed loop coordinates; common static definition. | 2,3 |
| Q11 | Nested loops repeat the same inner name | Parent iteration disambiguates identities and memberships. | 2,3 |
| Q12 | Keyed fan-out A/B concurrently, change source order | Item identity stable; source-order display/results remain explicit; no scope leakage. | 2,3 |
| Q13 | String key "1", numeric key 1 in Fuwen; duplicate/null/native invalid keys; positional profile | Typed tokens distinct where supported; invalid keys reject before work; positional identity explicitly tagged. | 2,6 |
| Q14 | All selected children pending; one child then runs | Pending then Running; active keys accurate; no execution edge from group order. | 5 |
| Q15 | Signal, delay, retry, child wait; authority approval pending/ready | Waiting with correct safe reasons and existing wake behavior; no synthetic approval authority. | 3,5 |
| Q16 | Terminal child failure and nested failure while sibling runs | Failed summary plus running count; failure propagates without duplicating counts. | 5 |
| Q17 | Transient failure parked for retry; successful later attempt | Waiting→Running→Completed as facts allow; same occurrence; attempt not extra progress. | 3,5 |
| Q18 | Failed revision selectively restarted, new revision succeeds; compare separate fallback that retains a failed current step | Restarted revision can complete; fallback preserves mechanical Failed group alongside the run's policy outcome; no scheduling effect. | 3,5 |
| Q19 | Completed and cancelled descendants; cancel run while sibling is active | Running/Waiting during settlement, then Cancelled; already-complete independent group stays Completed. No new group-cancel API assumed. | 3,5 |
| Q20 | Dependency block, quiesced generation, run failure outside subtree | Pending/Blocked reason for unfinished affected work; never alter persisted StepStatus to Blocked. | 5 |
| Q21 | Empty open occurrence versus entered sealed empty occurrence versus unselected definition | Pending/unknown, Completed/zero, and no runtime occurrence respectively. | 3,5 |
| Q22 | Three completed known members with more loop iterations possible | Known=3, Completed=3, total=null, expansion open, not Completed. | 3,5 |
| Q23 | Fan-out enumeration cut after a committed prefix, then recovered and sealed | Prefix is partial/open; recovered list exact; no missing or duplicated items; total known only after complete selection. | 3,5 |
| Q24 | Legacy serialized definition and canonical fingerprint fixtures | Byte/digest contracts unchanged by absent/null group additions. | 1,6 |
| Q25 | Old compiled binary/custom store with no new group capability | Ungrouped profile works; group-bearing registration rejected before dispatch. | 1,3 |
| Q26 | Metadata contains large spec, secret-like argument, absolute source path | Reject/exclude by safe provenance policy; no blind copying to events/snapshot. | 1,4,6 |
| Q27 | Lease expires/reclaimed, and stale worker attempts group writes | Same occurrence; stale materialization/seal rejected with no rows/events. | 2,3 |
| Q28 | Process dies while group pending, between run insert and manifest installation | Resume completes exact installation before callbacks; offline read reports installation incompleteness honestly. | 3 |
| Q29 | Process dies before/after child-step claim/membership commit, including Running member | Both rows or neither; restart reuses committed result and preserves exact membership. | 3 |
| Q30 | Process dies while waiting on signal/approval/child | Same wait correlation/deadline and group; fresh process observes it without executing recovery. | 3 |
| Q31 | Process dies after partial loop/fan-out expansion and at loop commit/final boundary | Correct occurrence separation and open/closed state; no repeated committed effect receipt. | 3 |
| Q32 | Process dies after failure, then selective restart | Prior evidence readable; new current revision attached correctly; stale closure invalidated. | 3 |
| Q33 | Change controlling condition revision and take a different branch | Former branch remains historical, not current pending work; closure/effective counts refer to new selection only. | 3,5 |
| Q34 | Reopen schema-5/6 fixtures, upstream authority-schema-7 fixture, new empty DB | Additive upgrade preserves IDs/results/events/cursors/auth state including authority-subject binding; no roots/members added to old runs. | 3 |
| Q35 | Failure at schema creation/version update; old schema-6/7 binary opens group-schema DB | Full rollback on failure; old worker rejects new schema; no silent downgrade or reuse of the authority schema number. | 3 |
| Q36 | Fork copies completed work; restart/new execution generation | New run-bound occurrences/memberships; original producer/source provenance unchanged; one progression owner retained. | 3 |
| Q37 | Snapshot while writer updates group+step+event; include approval facts | One consistent boundary; returned watermark and rows never mixed; no raw authorization payload. | 4,5 |
| Q38 | Nested progress, control helpers, repeated attempts and child runs | Work leaves counted once; Control failure visible; child-run progress not folded into parent count. | 4,5 |
| Q39 | Permute siblings, retry history, current active set | Reducer deterministic at fixed time; no model calls; active set supports multiple concurrent steps. | 5 |
| Q40 | Paged tree during updates, snapshot-only generation change, cache expiry/process loss | Stable token snapshot or explicit resync; no page assembled under a falsely stable event watermark. | 4 |
| Q41 | Missing run/group, unsupported reader, oversized result, truncated page, purged run | Distinct not-found/unsupported/limit/partial results; never empty-tree success for an error. | 4 |
| Q42 | Macro contains native child invocation; child has its own groups | Parent `:start`/`:wait` belong locally; child tree owns its own IDs; no cross-run membership. Restart creates normal new child identity. | 2,3,4 |
| Q43 | Compile SupervisedCoding and substantially different TDD plus AlienProcess42 | Same generic Logical/Macro representation; no runtime macro-name switch; safe provenance survives restart. | 6,7 |
| Q44 | Repeated/nested Fuwen macros plus repeat-backed group occurrences | Deterministic expansion and mapper exactness; restart works from admitted plan with compiler/library unavailable. | 6,7 |
| Q45 | Fuwen macro in keyed fan-out with two inner activities; kill after first activity commit | Each promised durable child has an actual independent boundary, no repeated committed first effect. Current item-only adapter cannot pass; reject interim or deliver versioned lowering. | 6,7 |
| Q46 | Different macro/library content or group manifest under old run/admission | Reject altered binding; preserve old run/bytes; no live re-expansion or retrospective metadata restamping. | 6,7 |
| Q47 | Fresh external consumer resolves exact candidate packages on .NET 8/10 and both OS jobs | Correct closure, API/binary compatibility and schema profile; no reliance on a source-tree project reference. | 6,7 |

For Q07, occurrence v1 excludes ManifestDigest. Given the same run, GroupKey,
explicit parent occurrence and coordinates, display/provenance or unrelated
sibling changes do not change its ID. The immutable manifest pin is separate:
re-registration with changed canonical bytes still conflicts for an admitted run.
The initial design included the digest in identity; ZG-2/3a supersedes that rule.

For Fuwen Q43-Q45, arbitrary nested control-flow combinations not supported by
the selected adapter must be negative preflight cases until implemented. Native
Zhinu child-workflow tests do not require adding Fuwen child syntax. Include a
domain-specific approval procedure as a third substantive vocabulary fixture;
AlienProcess42 additionally proves that macro names carry no semantic meaning.

## Existing regression anchors

These suites exist at the inspected commit. Extend/use them; do not replace their
assertions with group-specific behavior:

- Core: `PublicSurfaceTests`, `ArchitectureTests`, `WorkflowRegistryTests`,
  `StateMachineTests`, `WorkflowEventDurabilityTests`.
- SQLite: `DurableLoopTests`, `DurableNestedLoopTests`, `DurableLoopRecoveryTests`,
  `DurableLoopAdministrationTests`, `DurableLoopCancellationTests`,
  `FanOutKeyedTests`, `RestartStepTests`, `DependencyScopeTests`,
  `GenerationSchedulingGuardTests`, `LateCompletionEvidenceTests`,
  `ChildSemanticsTests`, `ChildCrashRecoveryTests`, `ChildTraceContinuityTests`,
  `WorkflowRunSnapshotTests`, `WorkflowEventPageTests`, `EventExportTests`,
  `RunGraphCliTests`, `SchemaCompatibilityTests`,
  `WorkflowAuthorizationMigrationTests`, `WorkflowAuthorizationFencingTests`,
  `WorkflowAuthorizationRecoveryTests`, `RetentionTests`, `CompensationTests`.
- The concurrently landed AE-6.2 baseline also adds `AuthoritySubjectBindingTests`;
  keep its nullable-binding, idempotency and migration guarantees green.
- Fuwen: `FuwenZhinuStepMapperTests`, `FuwenZhinuRepeatTests`,
  `FuwenZhinuFanOutTests`, `WorkflowAdmissionTests`, `WorkflowPlanIdentityTests`,
  `RuntimeNodeIdentityTests` and source repeat/fan-out compiler suites.

## Commands and process-loss method

Run from each repository root. Establish a fresh baseline before editing code;
this documentation task did not execute these runtime suites.

```powershell
dotnet restore Penghou.Zhinu.slnx
dotnet build Penghou.Zhinu.slnx -c Release --no-restore
dotnet test tests/Penghou.Zhinu.Tests/Penghou.Zhinu.Tests.csproj -c Release --no-build --no-restore
dotnet test tests/Penghou.Zhinu.Sqlite.Tests/Penghou.Zhinu.Sqlite.Tests.csproj -c Release --no-build --no-restore
dotnet format Penghou.Zhinu.slnx --verify-no-changes --no-restore
dotnet test Penghou.Zhinu.slnx -c Release --no-build --no-restore --logger trx --results-directory test-results
dotnet pack Penghou.Zhinu.slnx -c Release --no-build --no-restore -o artifacts
```

Match `.github/workflows/ci.yml` for the OS matrix, .NET 8/10 installation and
`eng/Verify-WorkflowPackageClosure.ps1` /
`eng/Verify-LegacyWorkflowConsumer.ps1`; derive PackageVersion from
`Directory.Build.props`, not this document. In Fuwen, restore/build its solution
then test `tests/Penghou.Fuwen.Zhinu.Tests/Penghou.Fuwen.Zhinu.Tests.csproj` against
the exact intended package closure, followed by its full CI checks.

Extend the existing process probe with explicit group fault points. Process A
writes a known run ID and is killed at an armed before/after-commit boundary.
Process B opens the same test DB with no inherited state, reads and asserts the
tree first, then resumes execution. A fake effect ledger outside process memory
detects duplicate committed effects. Assert revision/member/provenance identity
and count semantics before and after resume. Use deterministic barriers and
existing time-provider patterns; do not rely on sleeps as correctness evidence.

Retain per-case TRX/logs, OS/TFM, commit IDs, candidate package hashes, schema
version, fault point, durable row/event assertions and expected effect receipts
under `docs/qualification/activity-groups/`. Failed/unsupported cases remain
visible. A no-build run over stale binaries is not evidence for changed source.
No group feature checkbox is complete until its required positive, negative and
process-loss cases have evidence.
