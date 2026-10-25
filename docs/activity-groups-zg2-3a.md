# ZG-2 + ZG-3a: implementation contract and checkpoint

This records the historical foundation profile. [ZG-3b.1](activity-groups-zg3b1.md)
subsequently adds schema 9 and opt-in root native fan-out; its narrower support
supersedes this checkpoint's blanket fan-out rejection, without changing v1 IDs.
[ZG-3b.2](activity-groups-zg3b2.md) adds native grouped forks using those same
root/loop bytes and destination RunId lineage, with separate evidence.

Base: `d2821b20a3ae767aa24627cd345f1880416829bc`.
Branch: `codex/zhinu-activity-groups`.
Implementation: `130e927f2d08f803e69a2e5f4c437e4e1bbacb30`.
Status and exact support boundaries: [qualification](qualification/activity-groups/zg2-3a.md).

## Identity reconciliation before implementation

The source map was checked before adding occurrence code:

| Existing concept | Source | Group mapping |
| --- | --- | --- |
| Run | WorkflowRun.Id | Occurrence owner; never inferred from display/session text. |
| Logical step and revision | WorkflowStepRun: RunId, StepKey, Id, Revision | Membership attaches to the exact revision row. |
| Retry attempt and lease | WorkflowStepRun.Attempt, LeaseGeneration | Excluded from occurrence identity; retries retain membership. |
| Loop iteration | DurableLoopIterationIdentity and WorkflowLoopIterationReference | Reuse the ordered enclosing loop-name/one-based-iteration chain; do not parse DisplayPath or arbitrary step keys. |
| Nested loop | DurableLoopScope.ParentIteration | Existing typed ancestry identifies the enclosing coordinate prefix. |
| Native fan-out | WorkflowContext.FanOutAsync creates prefix.key or prefix.index steps | No reusable typed item-scope value or durable enumeration manifest exists. Per-item group scopes remain deferred, without guessing coordinates from dotted keys. |
| Restart | New step Id/Revision; same RunId/StepKey | Keep historical membership and inherit the same occurrence for the new revision. |
| Fork | New RunId, copied step results | Grouped fork is rejected before writes in this foundation; future support must rebind destination occurrences. |
| Child | Separate RunId; parent start/wait steps; ChildRunCoordinator hashes parent run/key/start revision | Child manifest belongs to its own run; never use cross-run group parents. |

The loop wrapper currently rebuilds StepOptions and omits authorization fields.
When adding group propagation it must also preserve those existing declarations;
dropping declared authority is not acceptable as a grouping side effect.

## Frozen foundation decisions

- Add an optional immutable manifest-provider capability to registration, without
  requiring old custom registrations/registries/stores to add abstract members.
- Add a nullable immutable run manifest digest marker. It is separate from
  DefinitionFingerprint and MetadataJson. A grouped marker with a missing or
  changed registration manifest/store capability fails before workflow callbacks.
- The group-capable store creates run, manifest and the existing start event in
  one transaction. There is no run-with-missing-manifest admission window.
- Explicit context factories create group occurrence references. Root groups use
  an empty execution coordinate; iteration factories use the current existing
  typed loop iteration. Child groups require the corresponding explicit parent
  occurrence and validate same-run ownership and ancestor coordinate compatibility.
- Occurrence identity is domain/version-separated SHA-256 of length-delimited
  RunId, GroupKey, explicit parent occurrence identity and existing loop coordinate
  components. It excludes Attempt, Revision, leases, display/macro text and manifest
  digest. This sharpens the earlier design: the digest pins immutable annotation
  content separately, so display metadata does not become occurrence identity.
- StepOptions carries optional immutable binding of the ZG-1 membership value to
  the selected occurrence. Work/Control changes only classification, never dispatch.
- The normal claim transaction validates the run pin, inserts/compares occurrence
  ancestors and inserts/compares revision membership. Reuse may not rewrite it;
  ungrouped membership remains null. Existing fences and authorization order apply.
- SQLite schema 8 adds immutable manifest, occurrence and membership storage plus
  the nullable run marker. Preserve the 5→6→7→8 chain and old-worker rejection.
- Store point reads support reconstruction/debugging. No public hierarchy/tree
  projection, aggregate state, progress, UI or Fuwen expansion is in this slice.

## Foundation scope boundaries

Qualified targets: static nested groups, mixed grouped/ungrouped steps, ordinary
retry/revision membership, root/loop iteration occurrences and real process loss.
Child registration admission must honor its own manifest or fail closed, including
custom registry paths. Parent boundary grouping and broader child operation
recovery remain separately qualified work.

Grouped forks and all native FanOutAsync overloads carrying StepOptions.Group
reject explicitly before affected work, including empty collections. Ordinary
ungrouped fan-out remains valid inside a grouped run. There is no per-item scope
API or silent flattening. Dynamic selection/closure, full loop/fan-out partial
expansion recovery and higher-level group queries remain later ZG-3+ work.

The [evidence](qualification/activity-groups/zg2-3a.md) records exact tests/packages
and distinguishes SUPPORTED from NOT YET QUALIFIED. Continue from the
[self-contained handoff](activity-groups-handoff.md).

## Using the explicit API

```csharp
var manifest = ActivityGroupManifest.Create([
    new ActivityGroupDefinition("delivery", ActivityGroupKind.Logical, "Delivery"),
    new ActivityGroupDefinition("coding", ActivityGroupKind.Macro, "Coding", "delivery",
        provenance: new ActivityGroupMacroProvenance("AnyMacroName", "definition-pin", "call-site"))]);
registry.Register(new WorkflowRegistration<string, string>(
    new WorkflowDefinition { Name = "example", Version = "1" },
    () => new ExampleWorkflow(), manifest));

// Inside ExampleWorkflow.RunAsync:
var delivery = context.GetActivityGroup("delivery");
// Inside the existing durable loop's body:
var coding = iteration.GetActivityGroup("coding", delivery);
await iteration.StepAsync("edit", input, (value, step, ct) => EditAsync(value, ct),
    new StepOptions {
        Group = new ActivityGroupStepBinding(new ActivityGroupMembership("coding"), coding)
    }, cancellationToken);
```

The factory creates an immutable occurrence reference. Its first durable member
claim materializes the occurrence and required ancestors. Merely creating an
unused reference does not insert a row or imply that expansion is closed. There
is no ambient scope and no automatic membership for loop control steps.
Loop wrappers carry the actual typed iteration into step validation. A step can
use an occurrence in its own or an enclosing scope; sibling/future iterations
cannot share an iteration-specific binding. Use `iteration.StepAsync` for loop
members; the root `context.StepAsync` surface accepts root-scope occurrences.

## Occurrence v1 bytes

Hash UTF-8 `penghou.zhinu.activity-group-occurrence.v1\n`, then four fields:
canonical lower-case Guid `D`, GroupKey, parent full occurrence ID (nullable),
canonical coordinate JSON. Prefix each non-null UTF-8 field with its unsigned
32-bit big-endian byte length; null is `FFFFFFFF`. Return full lower-case SHA-256
hex. Root coordinate is `[]`; loop frames are
`[{"loop":"review","iteration":2}]`, outermost first, invariant positive integers,
no whitespace. Existing loop-name validation restricts names to ASCII letters,
digits, `_`, `-`, `.`. No arbitrary step-key parsing is involved.

Independent golden vectors use run `ac09fa13-1206-402f-8657-fb318bf3926e`:

- `root`, null parent, `[]` → `fa9e60f647bdfc20142cec8bd019d4dbf4ad6601f3cc3ca02335e3504aca2d18`.
- `work`, preceding root parent, `[{"loop":"review","iteration":2}]` →
  `f421f8e8cbf937c65c7fbf15832014662cc0da9766774b6450d48bf4071b0191`.

Bounds: ZG-1 manifests retain their 4,096-definition/128-depth/1 MiB ceilings;
occurrence ancestry and loop frames cap at 128, coordinate UTF-8 caps at 32 KiB,
and materialization caps at 65,536 occurrences per run. Reads restore/verify
canonical bytes, manifest pins, parent ownership and deterministic IDs. These
diagnostic point reads do not promise a multi-call snapshot during retention.
