# ZG-3b.3 qualification: fixed nested native repetition

Date: 2026-10-17. Status: **passed for fixed native nested repetition**.
Base: `ef6d2fe3bf8d7490d5e6e89fbd5406f457f85c7f`, verified clean on
`codex/zhinu-activity-groups` before implementation.
Implementation/package/test source: `4d24d6c4e25efc4efdf99b4e3f44f461e227019d`.
The commit containing this record adds evidence/docs only. Machine-readable
receipts, exact source/package hashes and before/after records are in
[zg3b3.json](zg3b3.json). Read the [supported contract and pre-code implementation
note](../../activity-groups-zg3b3.md) before applying this evidence.

| Gate | Result |
| --- | --- |
| Full Release regression | **1,531 passed**, zero failures/skips across 11 assembly/framework combinations on .NET 8/10 |
| New coverage | 16 semantic cases per framework: four scope/validation cases and twelve SQLite execution/retry/restart/process cases; **32 new executions** over the 1,499 baseline |
| Process loss | Seven boundaries per framework; **14 new cases**, each with fresh crash and recovery workers; all before/after receipts retained |
| Formatting | Full solution verify-no-changes passed |
| Packages | All seven unpublished `0.2.0-preview.4` candidates built from the exact implementation commit; every nupkg repository commit field matches it |
| Package consumer | Both frameworks passed, including fixed 3×2 nested repetition with six unique leaves and three outer ancestors |
| Binary compatibility | Unchanged published `0.1.0-preview.15` consumer passed with candidate runtime substitution on both frameworks |
| Schema compatibility | Schema remains **9**; retained schema-5→9 migration and old-worker rejection passed with four step rows retained; genuine grouped schema-8→9 migration also passed in the full suite |
| Review | Luna found no blocking issue; explicit entry-state assertions added to strengthen the process boundaries |

The final suite includes 137 core and 583 SQLite tests on each framework, plus
the existing hosting, ASP.NET Core, agent and telemetry suites. All existing
ZG-3b.1 and ZG-3b.2 cases pass, including fourteen fork process-loss cases. This
does not qualify combined fan-out/fork or nested repetition/fork composition.

## Identity and scope

The existing native model already represents this composition. This checkpoint
adds the explicit nested profile, public XML explanation, executable shared
fixtures, tests, crash/recovery workers and package consumer. It changes no
runtime execution algorithm, coordinate encoder, occurrence hash, public signature
or persistence schema. Retained source hashes in the JSON prove that identity,
native loop execution, group persistence, schema and mandatory fork contract are
unchanged from the base. Root and single-loop v1 golden vectors remain gates.

Native `WorkflowLoopIteration.LoopAsync` retains its typed parent iteration.
The v1 coordinate walks all frames outermost first, using one-based native loop
iterations. Occurrence identity remains SHA-256 over length-delimited RunId,
group key, explicit parent occurrence ID and canonical execution coordinate.
Attempt, generation and payload equality do not define group identity.

The shared fixture has **three outer occurrences, six inner occurrences and
exactly six leaf occurrences**. Every leaf uses identical input. The same local
inner positions under different outer iterations have distinct two-frame
coordinates and parent IDs. Each leaf's immediate parent reconstructs its inner
occurrence; that parent's pointer reconstructs the corresponding outer occurrence.
Point reads validate those chains, and the raw occurrence count excludes hidden
orphan/duplicate records. Grouped entry activities expose real durable entry
boundaries; factories alone are references. The isolated package consumer also
qualifies lazy parent materialization directly from leaf claims.

## Retry, selective restart and completion

Two retry tests force one failure at a later inner leaf or an outer entry
activity. The retried callback retains step ID/revision, full coordinate,
occurrence and parent; attempt advances from 1 to 2. Completed siblings do not
run again. An outer orchestration delegate has no independent activity retry
identity under native semantics; no new outer-body retry policy is invented.

Selective restart at a later inner leaf or an outer commit retains every group
occurrence and historical membership. Invalidated member revisions retain their
logical occurrence; steps outside native invalidation remain unchanged. Callback
receipts restrict reexecution to invalidated members. Restarting the outer commit
reuses completed nested state according to the existing dependency graph.

Inner commits depend on body activities; each outer commit depends on its inner
final. Completed native conditions/commits/finals reconstruct state on replay.
The final state is inner 2 for all three loop instances and outer 3. Assertions
require six inner commits, three outer commits, three inner finals and one outer
final, with one event per completed boundary revision including restart history.
No aggregate group completion or no-runnable-work shortcut is introduced.

## Fresh-process loss matrix

Each framework runs seven cases, for **14 new process-loss cases** and **28 fresh
worker starts**. Crash workers exit **93**, recovery workers exit **0**. The parent
test captures durable state before recovery. Each recovery worker creates new
engine/store/workflow/typed scopes, so no process-local reference survives.

| Mode | Deterministic barrier and state before recovery | Recovery proof |
| --- | --- | --- |
| outer-ready | Outer 1 entry Completed; one outer occurrence; no inner creation | Completed outer entry reused, inner/future lineage created correctly |
| inner-ready | Outer entry Completed; inner 1 entry Running with durable inner membership; no leaf | Same entry ID/revision/parent; attempt increments; leaf materializes beneath it |
| early-running | Outer 1 / inner 1 leaf Completed; inner 2 leaf Running; no inner final or outer commit | Completed sibling reused; unfinished leaf retains lineage; no premature completion |
| later-running | Outer 1 committed; outer 2 inner 1 completed, inner 2 leaf Running | Completed outer work reused; partial outer identity preserved; outer 3 stays distinct |
| leaf-completed | First leaf Completed before first inner commit | Leaf reused; only native inner/outer control progression continues |
| inner-completed | First inner loop final Completed, before outer 1 commit | Completed nested work reused; outer progression follows its durable commit |
| outer-committed | Outer 1 commit Completed before outer 2 condition/occurrence creation | First outer body skipped; later outer/inner coordinates remain unique |

All preexisting occurrence IDs/coordinates/parents and exact memberships are
unchanged after recovery. Every completed step row retains ID/revision/attempt/
input/output/status. Running steps retain ID/revision with attempt +1. No completed
member callback runs again. At the end there are exactly 15 group occurrences and
15 current member activities, including exactly six leaf occurrences; all current
native steps are Completed and all loop finals hold the required state.

No sleep selects a boundary. Recovery uses the same advanced clock for lease
expiration and worker eligibility. The first exploratory harness used a future
recovery timestamp with a wall-clock worker, putting unfinished steps one minute
in the future beyond its 45-second timeout. That aborted run is not acceptance
evidence. Only the fixture clock and timeout worker cleanup were corrected; no
product implementation was repaired. The final full run is the acceptance source.

The first package/binary probe attempts stopped before consumer execution when
C: filled. Obsolete isolated Zhinu probe caches, identified by prior committed
receipts/generated assets, were removed; source and qualification records were
preserved. Both unchanged probes were rerun after space was recovered. Only the
successful probe receipts are acceptance evidence.

## Composition limits and remaining work

Keyed and positional grouped fan-out with a loop parent reject during plan
validation; simultaneous typed-loop and fan-out scope binding rejects. Existing
engine/store tests retain explicit rejection of grouped fan-out/fork copying.
Direct Group on fan-out stays invalid. Full ZG-3b.1 and ZG-3b.2 regressions are
required; the standalone fork destination model is preserved. Nested repetition
with forks is not newly qualified and no native fork contract changes here.

Evolving selection remains deferred after ZG-3b.3.

No added/removed/reordered/recomputed dynamic sets, arbitrary mixed compositions,
child-boundary qualification, public tree/query snapshot, aggregate state/progress
or Fuwen adapter work is claimed. Internal point reads preserve nested identity;
coherent public queries remain ZG-4. Continue with ZG-3b.4, 3b.5 children and 3b.6
consolidation before ZG-4/5, ZG-6 Fuwen and ZG-7 cross-repository qualification.
This is local Windows .NET evidence, without Linux/remote CI or publication claims.

## Reproduction

```powershell
dotnet test Penghou.Zhinu.slnx -c Release --no-restore --logger trx --results-directory artifacts/zg3b3-qualified-tests -v minimal
dotnet format Penghou.Zhinu.slnx --verify-no-changes --no-restore
dotnet pack Penghou.Zhinu.slnx -c Release --no-restore -o artifacts/zg3b3-packages
./eng/Verify-WorkflowPackageClosure.ps1 -ArtifactsDirectory artifacts/zg3b3-packages -PackageVersion 0.2.0-preview.4 -OutputDirectory artifacts/zg3b3-closure
./eng/Verify-LegacyWorkflowConsumer.ps1 -ArtifactsDirectory artifacts/zg3b3-packages -PackageVersion 0.2.0-preview.4 -OutputDirectory artifacts/zg3b3-legacy
```

Use a clean source checkout and separate output directories/caches for probe
runs. Build the seven candidates from the recorded source commit; do not reuse
packages from a previous checkpoint. The package consumer runs fixed nested 3×2
loops using only packages. The legacy consumer is built against published
`0.1.0-preview.15`, then executed unchanged with candidate runtime assemblies;
it also checks retained schema-5→9 migration and old-worker rejection. Genuine
schema-8→9 grouped-row migration remains separately covered by the SQLite suite.
