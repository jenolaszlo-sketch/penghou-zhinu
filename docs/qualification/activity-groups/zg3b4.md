# ZG-3b.4 qualification: evolving selection through explicit immutable epochs

Date: 2026-10-17. Status: **passed for explicit immutable selection epochs**.
Base: `9be3308555e2fe2c4cc169391a4baf6cc32a8d91`, verified clean on
`codex/zhinu-activity-groups` before coding.
Implementation/package/test source: `bfa1bf6c71b5bf4a29fe09a508f9a237b0f4cea6`.
The commit containing this record adds evidence/docs only. Exact source/package
hashes, raw durable snapshots and compatibility receipts are in
[zg3b4.json](zg3b4.json). Read the [contract and pre-code note](../../activity-groups-zg3b4.md).

**Nested repetition required no runtime algorithm, schema, or identity redesign.**
This result from ZG-3b.3 must remain prominent in ZG-3b.6 consolidation.

| Gate | Result |
| --- | --- |
| Full Release suite | **1,571 passed**, zero failures/skips across 11 assembly/framework pairs on .NET 8/10 |
| New coverage | 40 executions: 18 SQLite cases and two core identity cases per framework |
| Process loss | 16 positive recovery cases plus two omitted-history rejection cases; 36 fresh worker starts |
| Formatting | Full solution verify-no-changes passed |
| Packages | All seven unpublished candidates built; each nupkg repository commit matches the source SHA |
| Isolated package consumer | Both frameworks passed, including five evolving immutable epochs and all previous package profiles |
| Legacy binary/schema | Unchanged published 0.1.0-preview.15 consumer passed candidate substitution, retained schema-5→9 migration and old-worker rejection on both frameworks |
| Schema | Remains 9; grouped schema-8→9 migration and prior group/fork/nested/authority cases passed |

The JSON retains 16 positive recovery receipts and 20 semantic receipts, including
the two negative fresh-process omission proofs. SQLite passed 601 cases and core
139 cases per framework. No product runtime algorithm, schema or identity redesign
was required for this bounded profile.

## What is qualified

Changing membership is a **new explicit durable epoch**, with its own stable
native fan-out prefix and completed selection step. Replaying an old epoch uses
its historical selector output. It does not reinterpret an old position, replace
an admitted plan, infer an epoch from today's clock/set, or erase old work.
This is a bounded authoring/qualification profile built from existing public
steps and root fan-out APIs, not a new first-class epoch or replanning API.

Schema **9**, occurrence v1/v2 encoders, native fan-out/loop algorithms and group
persistence are unchanged. The only library source change is explanatory XML.
Existing v2 scope/key-or-index coordinates distinguish identical/reappearing items
across epochs. There is no new epoch hash field, coordinate version, parent rule,
table or migration. Root and loop v1 compatibility remains independently covered.

The five pinned epochs use the same group definition and explicit root parent:

| Epoch | Ordered selection | Proof |
| --- | --- | --- |
| 1 | A, B, C | Original history; B completes while C remains Running |
| 2 | A, C, D, E | Expansion; completed B excluded without relabeling; D Running, E unclaimed |
| 3 | C, A | Contraction while earlier C/D remain Running and E has no step claim |
| 4 | A, C | Reordering under a distinct immutable epoch |
| 5 | B, A, C | B reappears with identical item content and a new occurrence |

The normal case admits **14 distinct item occurrences plus one root**. All item
payloads are identical; the whole B input is also identical between epochs 1 and
5, but its native key/coordinate/occurrence differ. Keyed and positional cases
both pass. Native results follow each ordered durable selector output. Exact old
selection JSON retains order; canonical keyed plan/item evidence is sorted and
does not itself carry a historical source-order column.

The durable schedule pins epoch identities. Recovery deliberately changes today's
schedule and selectors: completed schedule/selection callbacks do not run again,
even for a selected epoch that had not yet admitted children. Existing epochs
retain their original ordered output and immutable expansion. Only previously
unselected future epochs invoke today's selector and admit its new output. The
item-key projection remains a deterministic pure function of stored input; native
replay still executes that projection and recomputes hashes for validation.

## Old unfinished work and completion

Later exclusion affects the new epoch only. It does not cancel or retire earlier
admitted work. Deterministic callback gates expose completed B, Running C/D and
unclaimed E while epoch 3 excludes D/E. After all gates release, the old scopes
finish their own required members and close; all five scopes must close before
the workflow can complete.

An additional negative process case deliberately omits the first unfinished epoch.
The worker returns normally from user orchestration but the existing completion
guard fails it with the exact `WorkflowConfigurationException` message:
`Workflow completed with an incomplete grouped fan-out enumeration.`
No missing-old-work, type mismatch or generic failure is accepted as this proof.
Old item/occurrence history remains intact and the omitted scope stays incomplete.

Cancellation/retirement and active-only state/progress semantics need separate
explicit policies. This profile does not invent them.

## Retry, restart and changed old decisions

An epoch-5 B failure retries with the same step ID/revision and logical occurrence;
completed members do not run again and selectors execute once per new epoch.
Selective restart at old B or D reuses all historical selector/schedule outputs
despite changed current callbacks. Occurrences, expansion/items and historical
memberships/closures remain intact; only the selected member revision executes.

Restarting an old selection revision and then expanding, contracting, changing B's
content or reordering positional values rejects before any new member callback
or group/expansion write. The prior completed selector revision retains its exact
ordered output. A pending replacement selector invalidates current closure without
rewriting historical closure records. No same-scope mutation rule was relaxed.

## Fresh-process matrix

Each crash worker exits **93**, and a separate fresh recovery worker exits **0**.
The parent captures durable state before recovery. Eight positive boundaries on
two frameworks give **16 successful recovery cases and 32 fresh worker starts**.
The two additional omission rejection cases use four more workers, for **18 new
process-loss cases and 36 fresh worker starts overall**. No sleep chooses a barrier.

| Mode | Durable boundary and interrupted state | Required recovery |
| --- | --- | --- |
| selection | Epoch 2 selector Completed; no epoch 2 expansion/occurrences/steps | Replay original selector output; materialize its original set before later current decisions |
| admit-transaction | Hard exit during epoch 2 item insertion, after an earlier insert in the same transaction | Atomic rollback leaves no epoch 2 header/items/occurrences; completed selector remains authoritative |
| admitted | Epoch 2 whole selected set/occurrences committed, before first member claim | Recover all admitted items without duplicating or reassigning occurrences |
| contracted-selection | Epoch 3 contraction durable; old C/D Running, E unclaimed | Preserve historical obligations and finish them with original membership |
| partial | Epoch 3 closed; older C/D still Running and E still unclaimed | A completed newer scope cannot erase incomplete older scopes |
| obligations-completed | Epochs 1–3 closed, before reorder/reappearance epochs | Reuse committed old work; only future epochs use today's selector |
| reordered | Epoch 4 closed, before epoch 5 reappearance | Preserve old order/history; admit a distinct future B occurrence |
| reappeared | All five epochs closed, before workflow completion | Zero item/selection/schedule callbacks; native workflow completion only |

Before/after receipts retain ordered selections, raw immutable expansion/item/
closure rows, occurrence coordinates/IDs/parents, exact step IDs/revisions/attempts/
input/output/status and membership. Every old row remains readable; completed
members do not execute again. Unfinished claimed members retain ID/revision and
increment attempt. Full selected sets are admitted atomically: partial
materialization here refers to native step/membership claims, never a partially
committed enumeration. Raw occurrence counts prevent hidden orphan rows.

## Scope limits and qualification discipline

The initial exploratory assertions read stored web-style JSON with default casing,
producing null fixture fields, and used a non-vacuous assertion on an intentionally
empty callback set. Corrected assertions only. The omission proof was strengthened
to run recovery in the same worker assembly and check the exact completion-guard
error, avoiding an accidental serialized type-ID mismatch. No product runtime was
repaired; exploratory runs are excluded from acceptance evidence.

Grouped fan-out remains root-only, and grouped fan-out/fork copying still rejects.
No new loop/fan-out or fork composition is claimed. Coherent epoch/history/tree
queries, active/history reducers, aggregate progress, child boundaries and Fuwen
lowering remain later work. Existing point reads distinguish every explicit scope;
this slice adds no public history/order projection. This is Windows .NET evidence,
without Linux/remote CI or package publication claims.

Next: **ZG-3b.5 child boundaries**, then 3b.6 consolidated qualification, ZG-4
queries, ZG-5 progress, ZG-6 Fuwen and ZG-7 cross-repository qualification.

## Reproduction

```powershell
dotnet test Penghou.Zhinu.slnx -c Release --no-restore --logger trx --results-directory artifacts/zg3b4-qualified-tests -v minimal
dotnet format Penghou.Zhinu.slnx --verify-no-changes --no-restore
dotnet pack Penghou.Zhinu.slnx -c Release --no-restore -o artifacts/zg3b4-packages
./eng/Verify-WorkflowPackageClosure.ps1 -ArtifactsDirectory artifacts/zg3b4-packages -PackageVersion 0.2.0-preview.4 -OutputDirectory artifacts/zg3b4-closure
./eng/Verify-LegacyWorkflowConsumer.ps1 -ArtifactsDirectory artifacts/zg3b4-packages -PackageVersion 0.2.0-preview.4 -OutputDirectory artifacts/zg3b4-legacy
```

Build all seven unpublished candidates from the exact source commit into fresh
directories. The isolated consumer executes five explicit evolving epochs and
all prior package profiles. The unchanged published `0.1.0-preview.15` consumer
checks binary substitution, retained schema-5→9 migration and old-worker rejection.
The full SQLite suite separately retains genuine grouped schema-8→9 migration.
