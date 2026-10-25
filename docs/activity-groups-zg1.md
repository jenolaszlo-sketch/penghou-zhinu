# ZG-1: immutable activity-group contracts

Implemented from base `b7f1009` on `codex/zhinu-activity-groups`, 2026-10-15.
This slice supplies annotation contracts and validation only. Runtime attachment,
occurrences, persistence, queries and aggregate state follow in ZG-2 onward.
It does not change SQLite schema 7, workflow execution or package references.

Qualification also required the binary compatibility repair described below;
that repair preserves existing execution/authority semantics.

## Public contract

All new types are in `Penghou.Zhinu`; source files are under
`src/Penghou.Zhinu/ActivityGroups/`.

```csharp
var manifest = ActivityGroupManifest.Create(
[
    new ActivityGroupDefinition("delivery", ActivityGroupKind.Logical, "Delivery"),
    new ActivityGroupDefinition(
        "delivery.coding", ActivityGroupKind.Macro, "Coding",
        parentGroupKey: "delivery",
        provenance: new ActivityGroupMacroProvenance(
            "AlienProcess42", "opaque-definition-pin", "coding-invocation"))
]);

manifest.ValidateMembership(new ActivityGroupMembership("delivery.coding"));
manifest.ValidateMembership(null); // Ungrouped work is valid.
```

`ActivityGroupDefinition`, `ActivityGroupMacroProvenance`,
`ActivityGroupMembership` and `ActivityGroupManifestLimits` are sealed immutable
classes with constructor validation and getter-only properties. The manifest
snapshots its input sequence and exposes a read-only collection of immutable
definitions. No caller-owned array or list can change the captured definition,
canonical JSON or digest after creation.

Definitions form a forest with no required synthetic root. Keys and parent keys
compare ordinally, with no Unicode/case normalization or whitespace trimming.
Nonblank leading/trailing whitespace is preserved as data, including in keys.
Logical groups reject macro provenance; Macro groups require it. Unknown macro
names are accepted as opaque data. Membership points to one direct group key and
has Work or Control role; absence of the entire value represents ungrouped work.
`ValidateMembership` checks only definition membership, not a runtime occurrence.

Duplicate keys, unknown parents, cycles, negative child ordinals and duplicate
child ordinals within a parent (including roots) are rejected. Declare distinct
ordinals for siblings; declaration enumeration order does not establish identity.
Validation walks the forest iteratively. Invalid inputs produce `ArgumentException`
or its argument-null/out-of-range subclasses without publishing a partial manifest.
Messages exclude submitted metadata values.

The only provenance fields are MacroName, MacroDefinitionFingerprint,
InvocationKey, optional MacroVersion and optional SourceReference. There is no
argument/prompt/credential bag. SourceReference is a plain relative slash-separated
display path: absolute roots, backslashes, URI/drive separators, percent/query/
fragment syntax, empty segments and `.`/`..` segments are rejected. It is never
dereferenced. Host policy must still decide whether any supplied display metadata
may be disclosed; bounded text is not automatically safe to share.

## Fixed v1 resource limits

| Resource | Hard/default maximum |
| --- | --- |
| Definitions | 4,096 |
| Nesting depth | 128, roots at depth 1 |
| Canonical JSON bytes | 1,048,576, excluding the digest domain prefix |
| GroupKey, ParentGroupKey, InvocationKey | 512 UTF-8 bytes each |
| DisplayName, MacroName, MacroDefinitionFingerprint, SourceReference | 1,024 UTF-8 bytes each |
| MacroVersion | 128 UTF-8 bytes |

`ActivityGroupManifestLimits` can narrow definition count, depth and canonical
bytes. Values must be positive and cannot widen these ceilings. Text limits are
fixed by v1. Count is checked both before known-size enumeration and incrementally
for lazy sequences, stopping at the first item above the limit. Text length is
checked before scanning or encoding, and invalid UTF-16/control characters are
rejected. Canonical writing checks the byte limit before every stream write and
flushes per bounded definition; it does not first allocate an unlimited document.
The empty forest is valid if it fits the host's canonical-byte limit.

## Canonical JSON and manifest digest v1

`ActivityGroupManifest.CurrentContractVersion` is 1. `Create` rejects other
versions. Encoding is independent of the existing workflow canonicalizers:

1. UTF-8 JSON without BOM or whitespace; all canonical characters are ASCII.
2. Top-level properties in order: `contractVersion`, `definitions`.
3. Definitions sorted by GroupKey with `StringComparer.Ordinal`.
4. Definition properties in order: `groupKey`, `parentGroupKey`, `kind`,
   `displayName`, `childOrdinal`, `provenance`. Kind is `Logical` or `Macro`.
5. Provenance properties in order: `macroName`, `macroDefinitionFingerprint`,
   `invocationKey`, `macroVersion`, `sourceReference`.
6. Optional values are explicit JSON nulls, not omitted. Numbers use invariant
   decimal integers. ChildOrdinal is included even when zero.
7. String escaping is fixed: printable ASCII is literal except `"` and `\`,
   which are backslash-escaped; every other UTF-16 code unit becomes uppercase
   `\uXXXX`. Valid surrogate pairs become two escapes. This does not use a
   runtime encoder's changing Unicode/HTML blocklist.
8. The lowercase, full SHA-256 digest is over the concatenation of UTF-8 bytes
   of `penghou.zhinu.activity-groups.manifest.v1` followed by one LF, then the
   canonical JSON bytes. The prefix has no BOM or terminator besides that LF.

The empty manifest has these exact bytes and digest:

```json
{"contractVersion":1,"definitions":[]}
```

```text
dd513a06f1fe4b2ab2757e3ef63aff378647cecc896a3054eeb4db80f467f5af
```

Display text, parent, child ordinal and provenance changes affect this annotation
digest. This is not a workflow execution fingerprint. The old declarative
canonical JSON/fingerprint is pinned independently by a regression vector.
Occurrence encoding remains the separate ZG-2 contract from the design; it must
not use concatenated display strings or this manifest digest as a substitute for
run/coordinate identity.

## Runtime API boundary for ZG-2/3

No StepOptions, workflow definition, registry, store or execution API accepts a
manifest in this slice. Creating a manifest cannot enable group execution or
silently flatten a grouped plan. In particular, there is no new interface for a
custom registration to advertise metadata that the engine might ignore.

The additive capability sketch remains:

```text
IWorkflowGroupManifestProvider  optional immutable manifest on registration
IWorkflowGroupRepository       optional durable installation/membership capability
IWorkflowGroupReader           optional coherent hierarchy projection capability
```

These names are design sketches, not declared APIs. Introduce registration
attachment only together with durable installation and fail-closed guards at all
start/resume/child/fork paths, including custom registries. Keep existing
IWorkflowRegistration/IWorkflowStore implementations unchanged for ungrouped
work. Do not put grouping metadata into mutable run MetadataJson or alter old
canonical serializers by appending nullable fields.

## Verification and next task

The legacy package probe exposed a prerequisite AE-6.2 regression: inserting the
authority binding parameter had removed the original seven-argument engine CLR
signatures. Restored required-argument forwarding overloads keep old compiled
StartAsync/StartHandleAsync/RunAsync callers working without ambiguous ordinary
short calls. The capability interfaces retain their old abstract slots; their
authority-aware defaults delegate only null bindings to old implementations and
reject non-null bindings. New engine implementations still honor bound requests.
`LegacyCapabilityDefaultsTests` covers this compatibility boundary.

The legacy probe also had a stale schema-6 expectation. It now accepts an explicit
`ExpectedCandidateSchemaVersion` (default 7) and checks the upgrade and old-worker
rejection against that pinned value. This changes the qualification expectation,
not the database schema. A later group migration must pass its own explicit value.

`ActivityGroupManifestTests` and `ActivityGroupManifestGoldenTests` cover the
contract portions of Q01-Q09/Q24/Q26, including immutability, forest rejection,
hard ceilings, exact-byte limits, Unicode/culture vectors, field-boundary
separation and the legacy workflow fingerprint. These are not evidence for
runtime occurrence, restart or query cases. Q25's external consumer compatibility
is checked through the existing package qualification probes.

Exact commands/results are recorded in
[ZG-1 qualification](qualification/activity-groups/zg1.md).
Next: implement ZG-2 occurrence identity and the smallest ZG-3 durable vertical
under the [stage plan](activity-groups-implementation-plan.md), with a separately
reviewed additive attachment API. Preserve all existing control-flow and
authority-subject behavior.
