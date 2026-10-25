# ZG-5 aggregate state and progress qualification

Status: **passed** on Windows, .NET 8 and .NET 10. Local qualification;
packages remain unpublished.

Qualified source: `01f2ac64173a625f3bf745704391871aac014a72`, based on
ZG-4 checkpoint `ac182f9437dad569e00145cc5fbdc8432d097c2d`.
The feature implementation is `e8bb89c975f32695364b7836e6ba41856e681094`;
the final source additionally hardens only the shared recovery fault-probe helper.
[Contract and bounded closure policy](../../activity-groups-zg5.md).
The following qualification commit changes documentation only.

## Projection contract

`WorkflowGroupStateReducer.Evaluate` is pure and uses an explicit observation
time. Selected declarations include pending members whose steps do not exist
yet. Current revisions alone contribute counts; attempts and retained history
do not. Work and Control have separate counters, but both participate in
Failed/Running/Waiting/Completed/Cancelled/Pending precedence. Dependencies may
refer outside the group without adding those steps to the group's counts.

Reasons distinguish approval, signal, child, retry, delay, other waits, ready/due
dispatch, expired leases, dependency blocks, failed-run blocks, quiescence and
supersession. These are observations, not authorization or execution commands.
Forward outcomes remain separate from compensation/run/generation context.
Incomplete data cannot yield Completed. Known lower-bound counts remain visible
with Partial/Unknown coverage and null totals.

The optional `IWorkflowGroupStateReader` adds `GetGroupStateAsync` without changing
the existing structural reader interface. It flattens the requested occurrence's
members and descendants from the cached ZG-4 data, preserving child-run isolation.
Snapshot token, watermark and observation timestamp accompany the result. Host
authorization, permission loss, expiry and reader/run token binding remain in
force. Later wall-clock time cannot reinterpret the captured state.

## Closure boundary

Successful run completion proves conservative closure for its materialized tree.
A current immutable fan-out admission can prove an individual item group's
complete selected membership before its step is claimed. It does not prove that
an arbitrary static ancestor has no future work. Other active code-first/loop
groups therefore remain Open with unknown totals. No names, lexical scopes or
Dispose/finally calls imply closure. The pure input can accept an explicitly
sealed empty selection from a caller with authoritative evidence; this adds no
new durable sealing API.

Superseded fan-out-only occurrences are historical, not current progress.
Ancestors with unresolved historical selection expose Partial coverage. Separate
explicit selection epochs retain all independently admitted obligations.

A native `StepOnly` restart can retain a completed loop composite and leave its
restarted member pending even when the run returns Completed. The aggregate
keeps that pending member visible. Restarting dependents invalidates the relevant
composites and reaches Completed after actual re-execution. This checkpoint
observes both policies without changing either.

## Qualification

The final complete suite passed **1,845 executions**, with no failures or skips,
across .NET 8/10: the ZG-4
baseline of 1,749 plus **96 new executions** (40 reducer and eight SQLite cases
per framework). The two existing fresh-process inspector cases additionally
check all 15 nested aggregate states, root count conservation and persisted
history without a registry/executor.

The focused run passed 80 reducer executions and 18 SQLite/inspector executions.
It covers every precedence rule, mixed roles/states, sealed/open/empty membership,
missing planned rows, historical revisions, retry attempts, deterministic sibling
permutation, expired leases, safe wait/approval readiness, dependency and
generation blocks, compensation context and invalid input. Real SQLite cases
prove nested flattening, child-run isolation, both restart modes, fan-out
selection before claims, historical selection, cancellation after independent
completion, authorization/expiry, and fixed snapshot observation time.

Formatting verification passed. Package qualification passed for seven unpublished
`0.2.0-preview.4` packages with repository source commits and 14 matching
packaged/build binary hashes. The NuGet-only
consumer checks nested and child-isolated summaries and all 14 independently
admitted selection-epoch members. The unchanged legacy consumer probe checks
binary replacement, schema 5→9 migration and old-worker rejection on schema 9.
Both consumer probes passed on .NET 8/10. The ledger retains 11 TRX files and
50 query/projection receipts, including the two fresh-process inspectors.

Two earlier attempts hit the existing 30-second timeout waiting for deliberate
`Environment.FailFast` worker exits (one during the full run and one in an
isolated rerun). The relevant recovery assertions were not reached in those
cases. The test-only helper now kills its own worker directly and parks if the
platform returns asynchronously, preserving the exact before/after-commit hard
loss boundaries without managed cleanup or crash-reporting delays. All eight
focused recovery executions then passed on .NET 8/10. The final source passed
the separate full-suite run recorded above; earlier failed TRX/logs remain under
`artifacts/zg5-qualified-tests` and `artifacts/zg5-recovery-rerun-tests`.

The [JSON ledger](zg5.json) retains raw TRX totals, projection/process receipts,
source/package/log hashes and both package-probe records. Ignored local artifacts
are checkout evidence, not remote CI or publication.

## Reproduction and remaining scope

```powershell
dotnet test Penghou.Zhinu.slnx -c Release --no-restore --logger trx --results-directory artifacts/zg5-reproduction-tests
dotnet format Penghou.Zhinu.slnx --verify-no-changes --no-restore
dotnet pack Penghou.Zhinu.slnx -c Release --no-build --no-restore -o artifacts/zg5-reproduction-packages
./eng/Verify-WorkflowPackageClosure.ps1 -ArtifactsDirectory artifacts/zg5-reproduction-packages -PackageVersion 0.2.0-preview.4
./eng/Verify-LegacyWorkflowConsumer.ps1 -ArtifactsDirectory artifacts/zg5-reproduction-packages -PackageVersion 0.2.0-preview.4
```

Run probes sequentially after tests, with space for isolated disposable caches.
Schema 9, identity encoders, native execution/restart/admission/authority behavior
and structural reader contracts are unchanged. No aggregate state is persisted.
Fuwen integration, group controls, wider dynamic composition, Linux/remote CI
and package publication remain separate gates. Next is ZG-6.
