# ZG-3b.1: native grouped fan-out

Implemented 2026-10-16. This is the first bounded ZG-3b profile; forks, nested
dynamic coordinates, evolving selection and child boundaries remain open.
See [qualification](qualification/activity-groups/zg3b1.md) for the exact tested
source, frameworks and compatibility evidence. The foundation's v1 bytes remain
unchanged. This profile adds schema **9** and occurrence-coordinate **v2**.

## Authoring and existing execution semantics

All four existing `WorkflowContext.FanOutAsync` overloads accept the additive
`StepOptions.FanOutGroup`. Functional/class implementations, native item keys,
bounded keyed chunks, positional parallelism and caller result order are retained.
One selected item represents one native durable step, not a new multi-step body.

```csharp
var selected = await context.StepAsync("select", input,
    (_, _) => Task.FromResult(new[] { "a", "b" }));
var results = await context.FanOutAsync("items", selected, item => item,
    (item, _, _) => Task.FromResult(item.ToUpperInvariant()),
    new FanOutOptions { MaxDegreeOfParallelism = 1 },
    new StepOptions
    {
        DependsOn = ["select"],
        FanOutGroup = new ActivityGroupFanOutBinding(
            new ActivityGroupMembership("work"), "select",
            context.GetActivityGroup("root"))
    });
```

The admitted manifest declares `work` under `root`. The selection step must
already exist and be completed at its current revision, and the author must
declare it in ordinary `DependsOn` (including an existing dependency scope).
No synthetic control activity or dependency edge is injected. The selection
step may have its own ordinary group binding. A top-level group needs no parent.
Parents are explicit references from the same run and manifest, at root scope.

Direct `StepOptions.Group` on fan-out still rejects. `FanOutGroup` on ordinary
`StepAsync` rejects rather than silently discarding it. An admitted manifest and
`IWorkflowGroupFanOutRepository` are required before selectors/item callbacks.
No ambient group scope or AsyncLocal inheritance is introduced.

The copied input list retains the caller's object references, as native steps
do. Admission hashes each serialized item; each item claim must match that hash
before its callback. A sibling mutation before claim fails closed. Callers remain
responsible for concurrent mutation after claim; callbacks are not promised a
deep-frozen object graph. Native forward retry/timeout/concurrency policy remains
runtime configuration, not a newly pinned executable fingerprint. Existing
registered compensation metadata and authority declarations are pinned and
checked; ordinary authorization still decides whether each callback may run.

## Durable selection and current completion

Before any item claim, one SQLite transaction verifies the live owner, lease,
generation, current completed selection revision and manifest pin, then installs:

- A bounded immutable expansion header anchored to that exact selection step ID
  and revision, with canonical selected-set bytes and SHA-256 digest.
- Every selected native step key, input hash, direct membership and deterministic
  occurrence, plus its explicit occurrence ancestors.
- The run's monotonic `GroupFanOutUsed` marker.

Keyed plans sort item tokens ordinally for comparison, so source reordering keeps
identities/digest while returned results follow the caller's order. Positional
plans bind each explicit zero-based index to its input hash; reordering values
changes the plan. Duplicate/invalid keys, conflicting native step keys and selected
sibling/self dependencies reject. Empty selection is validated and closed too.
Limits: 4,096 items/dependencies, 1 MiB canonical plan, 512 UTF-8 bytes per item
token/prefix, 1,024 per dependency, 65,536 occurrences/expansions per run.

Item claims atomically validate their expansion/item reference, current boundary,
input hash, types, implementation, authority, compensation and dependencies,
alongside existing membership and generation fences. An ordinary claim cannot
use a reserved selected key. The selected set cannot be inferred from claimed rows.

After every current item is completed, an append-only closure proof binds their
exact step IDs and revisions. An item restart invalidates the old proof without
deleting it; completion can append a proof for the new vector. Attempts do not
change occurrence identity. Restarting selection with the identical plan creates
a new expansion anchored to the new boundary and reuses compatible item results.
A changed selected set/input/parent/contract is explicitly rejected in this
profile, including after selection restart; evolving selection belongs to 3b.4.

Reads evaluate closure against current revisions in one SQLite read transaction.
The engine refuses successful workflow completion if a latest expansion is open,
including when workflow code swallows a fan-out failure. Rollback replays the
existing plan and existing compensation machinery without new admission/closure
writes. Retention cascades the evidence with its run. These diagnostic point reads
are not ZG-4 hierarchy projections or ZG-5 progress/status calculations.

## Identity and compatibility

Root/loop occurrence **v1** arrays, canonical bytes and SHA-256 domain remain
unchanged. New fan-out coordinates have this strict canonical envelope:

```json
{"version":2,"frames":[{"fanOut":"items","kind":"key","item":"a"}]}
```

`kind` is `key` for native string keys and `index` for canonical nonnegative
decimal positional indexes. The v2 SHA-256 domain is
`penghou.zhinu.activity-group-occurrence.v2\n`, followed by the same four
big-endian length-delimited UTF-8 fields as v1: canonical `D` RunId, group key,
nullable explicit parent ID, canonical coordinate. Null length is `FFFFFFFF`.
There is no dotted-key parsing, normalization, manifest digest, attempt, revision
or lease in occurrence identity. Independent golden vectors cover both kinds.

Schema 9 adds only enumeration/item/closure records and a monotonic run marker.
Supported migration is 5→6→7→8→9, retaining the literal authority/schema-8
intermediate migrations. Fresh and migrated databases both enforce marker
immutability. Ordinary/grouped creation rejects a caller-supplied true marker.
A marked run requires the new optional store capability on resume/administration;
existing foundation-only providers remain valid for the foundation profile.
Older binaries reject schema 9; no mixed old/new writers or downgrade is promised.
The seven candidate packages remain unpublished `0.2.0-preview.4` artifacts.

## Next independent proofs

Continue with 3b.2 grouped forks, 3b.3 nested repeated parents, 3b.4 evolving and
partial dynamic expansion across restart, 3b.5 child-workflow boundaries, then
3b.6 consolidated qualification. This profile supports one root fan-out frame;
it does not establish nested fan-out/loop combinations or typed Fuwen tokens.
ZG-4, ZG-5 and Fuwen integration stay deferred until the full selected dynamic
model is qualified. No fundamental contract redesign is required.
