# ZG-3b.5 qualification: native child-workflow group boundaries

Date: 2026-10-18. Status: **passed for native root child invocation boundaries**.
Base: `7d230a3ebcecb5ef9bdc733fb2e2fe41e1e087ca`, verified clean before coding.
Implementation/package/test source: `3ca11057c5c43463bc9c82304058d68e60b3433e`.
The commit containing this record adds evidence/docs only. Exact source/package
hashes, durable snapshots and compatibility receipts are retained in
[zg3b5.json](zg3b5.json). Read the [contract/pre-code note](../../activity-groups-zg3b5.md).

| Gate | Result |
| --- | --- |
| Full Release suite | **1,639 passed**, zero failures/skips across 11 assembly/framework pairs on .NET 8/10 |
| New coverage | **68 executions**, 34 SQLite cases per framework |
| Process loss | 16 recovery cases, ten distinct crash boundaries per framework, **38 fresh worker starts** |
| Formatting | Full solution verify-no-changes passed |
| Packages | All seven unpublished candidates built; nupkg repository commits match the source SHA |
| Isolated package consumer | Both frameworks passed, including independent grouped parent/child trees and all prior profiles |
| Legacy binary/schema | Unchanged published 0.1.0-preview.15 consumer passed candidate DLL substitution, retained schema-5→9 migration and old-worker rejection on both frameworks |
| Schema/identity | Schema **9**, manifest v1, occurrence v1/v2, serialized child start request and native child ID formula unchanged |

SQLite passed 635 cases and core 139 cases per framework. The JSON retains 16
process receipts and 48 semantic receipts. There is no new child identity scheme,
schema migration or child coordinator algorithm. The additive runtime change is
the two optional parent bindings and their prevalidation/forwarding to ordinary
step claims.

**Nested repetition required no runtime algorithm, schema, or identity redesign.**
Preserve this established ZG-3b.3 result prominently in ZG-3b.6 consolidation.

## Parent boundaries and child ownership

`ChildRunOptions.StartGroup` binds the existing parent `child:start` admission
step; `WaitGroup` binds its existing `child:wait` result step. The standard macro
profile uses Control and Work respectively. Both nullable bindings are explicit
and independent. They do not infer roles, membership or child grouping.

The matrix covers all eight combinations of parent start/wait membership and
child work grouping, plus a truly ungrouped parent registration invoking a
grouped child. Identical parent/child manifest bytes and definition keys still
produce separate run-local occurrence IDs and ancestry. Child work points only
to child-owned parents. Parent start/wait share their own parent occurrence.

Both parent bindings are validated before claiming start. Foreign-run and
wrong-manifest bindings reject before boundary/child creation. An iteration
occurrence passed through the untyped native StartChild surface rejects; it does
not establish a typed child invocation scope. Replay cannot remove historical
membership or change its role. Existing exact-revision membership rejects these
changes while retaining old steps, child references and group evidence.

Group metadata is absent from serialized `ChildStartRequest`. Independent SHA-256
expectations verify the unchanged child ID derived from parent RunId, invocation
key and start revision. The child independently captures its registration,
manifest, initial generation and tree.

## Retry, restart, forks, authorization and cancellation

Child activity retry uses the same step revision and occurrence. Restarting only
wait reuses the admitted child and completed child result. Restarting start
increments its native revision and admits a different child; historical child
records and old parent memberships remain intact.

Forking a static grouped parent at wait rebinds parent membership to destination
occurrences while retaining the reused source child reference and its original
ParentRunId. It does not reparent the child or copy its tree into the destination
parent. Forking at start reexecutes admission under the destination RunId and
creates a destination-owned child. Existing source membership remains unchanged.

Start, wait and child work retain separate authorization declarations. Denied
start admits no child; denied wait retains a pending admitted child without child
effects; denied child work fails normally without executing its callback. Allowed
child work sees its own execution identity and the native parent execution ID.
Terminal child failure propagates normally with durable membership retained.

A deterministic gate cancels a parent while grouped child work is entered.
Parent and child become Cancelled, membership/manifests remain, and repeated
cancellation emits one cancellation event per run. This proves the ordinary
successful cancellation path. Native subtree cancellation remains a sequence of
separately durable operations; crash-atomic subtree cancellation is not claimed.

## Fresh-process boundaries

Every independent case inspects durable rows after hard exit, before starting a
fresh recovery worker. Existing step IDs/revisions, memberships and occurrence
tuples must survive; completed steps remain unchanged. Point-read ancestry is
checked against raw occurrence counts so orphan/duplicate rows cannot hide.
Final parent/child run cardinality, manifests, output and initial generation are
also checked.

| Boundary | Durable state before recovery |
| --- | --- |
| Parent start claim transaction | Start step/event/membership/occurrences roll back; parent manifest remains; no child |
| Child manifest admission transaction | Parent start Running; child run/manifest/start event roll back together |
| Child generation creation | Child run/manifest/start event committed; zero child generations; orphan initial instance is recoverable |
| Parent start completed | Exact child ID/output committed; child pending; wait not claimed |
| Parent wait claim transaction | Completed start retained; wait claim/membership roll back |
| Child work Running | Parent wait and child work Running; both group trees and exact memberships already durable |
| Child completed | Child work/output completed; parent wait unfinished; recovery runs no child callback |
| Parent wait parked | Real peer owns and blocks child work; parent wait correlation committed |
| Child terminal plus wait Ready | Real peer completes child work, then hard-exits after the child-terminal/Ready transaction commits |
| Parent wait step completed | Wait consumed and parent result step committed; recovery reuses it and completes the ordinary tail |

Per framework, seven independent crash/recovery pairs use 14 workers. One
five-worker sequence repeats committed-start setup, starts a real peer child,
crashes a parent after parking, releases and crashes the peer after child/Ready
commit, crashes a parent after wait-step commit, then finishes in a fresh worker.
That is **19 workers per framework, 38 total**. The parked/Ready/wait-completed
sequence contributes one recovery test receipt with all intermediate snapshots;
it is not reported as three independent process pairs.

Recovery time is derived from the latest relevant persisted run **and step**
lease expiry and shared with the recovery engine. The peer's longer live lease
is preserved while the parent is recovered for parking. File/callback/repository
gates choose barriers; polling does not choose a crash by elapsed time. An
unfinished child callback may run again under existing retry/reclaim semantics;
completed work does not.

## Limits and next gate

This qualifies native root invocation boundaries. Typed loop/fan-out child
lowering, arbitrary mixed composition, coherent cross-run query/drill-through,
aggregate state/progress, Fuwen syntax/integration and cross-repository closure
remain open. Grouped fan-out/fork evidence copying still rejects. Windows local
qualification does not establish Linux/remote CI or package publication.

Next is **ZG-3b.6 consolidated dynamic qualification**, retaining each profile's
evidence and composition limits, then ZG-4 queries, ZG-5 progress, ZG-6 Fuwen and
ZG-7 cross-repository qualification.

Exploratory fixture compilation, authorization grant timestamps, native error
expectations, peer receipt writing and lease-clock mistakes were corrected before
the exact-source full suite. Those exploratory runs are not acceptance evidence.
No product defect or schema/identity redesign was required.

## Reproduction

From the implementation source on Windows with the pinned SDK/dependencies:

```powershell
$env:MSBUILDDISABLENODEREUSE = '1'
$env:DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER = '1'
dotnet test Penghou.Zhinu.slnx -c Release --no-restore --logger trx --results-directory artifacts/zg3b5-qualified-tests -v minimal
dotnet format Penghou.Zhinu.slnx --verify-no-changes --no-restore
dotnet pack Penghou.Zhinu.slnx -c Release --no-restore -o artifacts/zg3b5-packages
./eng/Verify-WorkflowPackageClosure.ps1 -ArtifactsDirectory artifacts/zg3b5-packages -PackageVersion 0.2.0-preview.4 -OutputDirectory artifacts/zg3b5-closure
./eng/Verify-LegacyWorkflowConsumer.ps1 -ArtifactsDirectory artifacts/zg3b5-packages -PackageVersion 0.2.0-preview.4 -OutputDirectory artifacts/zg3b5-legacy
```

Use new empty result/output directories for reruns so receipts cannot mix runs.
The evidence commit changes docs only; the package source is the implementation
SHA above. No package has been published by this qualification.
