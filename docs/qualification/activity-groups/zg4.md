# ZG-4 hierarchy query qualification

Status: **passed**. The clean full suite, formatting and both isolated package probes passed.

Source: `f911630e52743373ced9f5be0e8d33e1142e63b2`, built on
`26d471539925facb6b11ded7da33fc6b877c8767`. The implementation note is
[ZG-4](../../activity-groups-zg4.md). The following evidence commit changes docs
only. Packages remain unpublished `0.2.0-preview.4`; SQLite schema remains **9**.

## Evidence boundary

This checkpoint adds coherent hierarchy queries over the bounded ZG-3b profiles.
It keeps definition intent separate from entered runtime occurrences, including
unentered definitions and truly ungrouped steps. Direct member reads preserve
Work/Control roles, current and historical revisions, dependencies, wait facts and
safe approval facts. Fan-out reads retain complete selected membership and
revision-aware closure vectors. Child and source links preserve actual native
ownership; child trees require their own snapshots.

The SQLite capability uses one deferred read transaction with row/byte preflight
and narrow structural selects. The reader freezes nested collections and bounds
capture concurrency, cache count/bytes, lifetime and page size. Signed cursors
bind the reader-local snapshot and query. Expiry, eviction, changed run stamps,
permission downgrade and invalid cursors have explicit outcomes. Payloads and
execution-authority details are excluded; provenance and source references are
opt-in and subject to the host read policy on each request.

The new cases cover 128-deep mixed definition trees; legacy runs; mixed root
nodes; nested occurrence and member traversal; restart history; root and static
nested-parent fan-out; selected occurrences before later item claims;
current/historical closure; independent child runs and fork-reused children;
redaction, authorization revocation, bounded resources, indexed lookups,
malformed storage/provider facts, cancellation and process-local tokens.

The real SQLite interleave pauses the reader after its first row read while a
preopened second connection commits step-attempt and run-timestamp mutations.
The captured view retains old facts; a fresh view observes new facts at the same
event watermark. A separate generation/disposition case also changes safe facts
without advancing that watermark. Native engine restarts separately qualify
current versus historical revisions. Two new fresh-process inspectors reopen a
completed 3×2 nested run without a registry or executor, reconstruct its 15
occurrences/members, and reject the prior process's token. These are inspection
workers; earlier hard-loss execution proofs retain their own records.

## Checks

The clean full suite passed **1,749 executions**, with no failures or skips, across eleven assembly/framework
pairs. This includes **102 new query executions**: 25 core reader cases and 26
SQLite cases on each framework. The ledger retains 36 query receipts, seven package
hashes, and 14 packaged binaries matching the tested build outputs. The 1,647-case baseline includes the eight ZG-3b.6
guard executions alongside the previously qualified 1,639-case suite.

Formatting passes on the final source. Seven candidate packages were created
without rebuilding the test binaries. Their isolated NuGet-only consumer passes
on .NET 8/10, including query pagination, nested occurrence identity, child links,
read-local token rejection and the existing durability/authorization cases. The
unchanged legacy probe passes precompiled consumer replacement, schema 5→9
migration, and safe old-worker rejection on schema 9.

An earlier final-source run encountered four SQLite disk I/O/full failures while
isolated probe caches were present. Completed probe caches were removed,
including expendable caches from earlier completed qualification probes. All
TRX, package, consumer and receipt outputs were retained. The final record uses
the complete clean rerun on the same source and binaries, without rebuilds or
restores competing for disk space. The earlier failed attempt remains under
`artifacts/zg4-final-qualified-tests`; it is not counted as passing evidence.

The [JSON ledger](zg4.json) retains source/artifact hashes, raw TRX totals, query
receipts, package source commits, probe records and log hashes. Local ignored
artifacts are evidence for this checkout, not portable CI results.

## Reproduction

```powershell
dotnet test Penghou.Zhinu.slnx -c Release --no-restore --logger trx --results-directory artifacts/zg4-reproduction-tests
dotnet format Penghou.Zhinu.slnx --verify-no-changes --no-restore
dotnet pack Penghou.Zhinu.slnx -c Release --no-build --no-restore -o artifacts/zg4-reproduction-packages
./eng/Verify-WorkflowPackageClosure.ps1 -ArtifactsDirectory artifacts/zg4-reproduction-packages -PackageVersion 0.2.0-preview.4
./eng/Verify-LegacyWorkflowConsumer.ps1 -ArtifactsDirectory artifacts/zg4-reproduction-packages -PackageVersion 0.2.0-preview.4
```

Run package probes after tests finish and provide disk space for their isolated
caches. They do not publish packages. The historical ZG-3b.6 exact-source audit
continues to identify its own earlier source; it does not qualify later changes.

## Remaining boundary

No schema, identity encoding, scheduling, fork or admission algorithm changed.
The existing occurrence hash helper became internal so the reader can validate
provider identities with the same encoder. Windows local .NET qualification does
not establish Linux/remote CI, publication, aggregate lifecycle/progress,
Fuwen integration, group controls or additional dynamic composition profiles.
Next is ZG-5 over these facts, preserving all accepted runtime boundaries.

**Nested repetition required no runtime algorithm, schema, or identity redesign.**
