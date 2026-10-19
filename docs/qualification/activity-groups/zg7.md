# ZG-7: consolidated activity-group qualification

Date: 2026-10-20. **Selected bounded profiles qualified on Windows and Linux, .NET 8/10.**
The selected profile and explicit exclusions are recorded in the
[evidence ledger](zg7.json). Packages remain unpublished.

Exact native runtime/package source is
`246d535679cca2ca5130ecfad62437dbb1fdd81d` (`0.2.0-preview.9`). Exact final Fuwen
runtime/package source is `535f82e2bd70d9a163b27be8671d82c75084a0dd`
(`0.1.0-preview.18`). Preview.17 was an intermediate qualification candidate;
preview.18 additionally rejects potentially empty grouped item paths before
storage. Earlier static/repeat/fan-out archives retain their original pins.

## Selected contract

Logical and Macro groups remain generic definition metadata with explicit
run-local durable occurrences, parent occurrence IDs and innermost membership.
Native fan-out, separate-run forks, fixed nested repetition, explicit immutable
selection epochs and child boundaries retain their separately qualified profiles.
**Nested repetition required no runtime algorithm, schema, or identity redesign.**
No schema change beyond the existing schema 9 is introduced here.

Fuwen supports local immutable macro expansion, nested static groups/macros,
static conditions/checkpoints/waits, single-level grouped repeat, and one fixed
keyed fan-out with independently durable item providers. Native capabilities do
not imply DSL support. Nested/mixed dynamic DSL scopes, evolving selection,
fan-out item waits/checkpoints/value merges, grouped coordinated inference and
item-local fallback remain preflight rejections. Fuwen has no child-workflow
syntax. The Q43–Q45 contract explicitly permits these unsupported combinations
as negative preflight cases; they are not silently flattened or counted as
positive execution coverage.

Public inspection preserves Work/Control classification and mechanical native
state. Provider failure envelopes are not domain success/failure decisions in
Zhinu. An independently completed fan-out item and nested static groups remain
Completed while a sibling runs or is cancelled; an open parent still has unknown
total. The seal is derived once from current selection/dependency/member facts.
Optional snapshot hashes preserve old constructor ABI and older providers remain
conservative when evidence is absent.

## Added cross-repository proofs

- **Q43:** real SupervisedCoding, TDD, both BDD branches, domain-specific approval
  and AlienProcess42 compile and execute through the same representation. These
  fixtures differ in executable shape rather than merely renaming one macro.
- **Q44:** nested macros within two repeated iterations survive hard process loss
  with exact occurrence parents/memberships, mapper coverage and external effect
  receipts. The new worker receives only immutable bundle bytes and host catalogue,
  with no source compilation or macro-library lookup.
- **Q45:** the retained two-operation keyed body probe kills the first worker after
  operation one commits and before operation two executes. Recovery preserves the
  first boundary and effect. Preview.18 additionally rejects a possibly empty
  selected body path before writes, while both-branch provider work stays supported.
- **Q46:** changed execution and manifest-only changes cannot resume an old parked
  run; original bundle bytes, hierarchy, memberships, status and provider count
  remain unchanged.
- **Q47:** both fresh package-only consumer projects run on .NET 8/10 and both OS
  jobs use the same Ubuntu producer archives. Verification checks all eleven
  archives, exact versions/source commits, resolved cache hashes, absent project
  references and four clean consumer TRX receipts. Both OS jobs passed using
  identical Ubuntu producer archive hashes.

The ledger maps every Q01–Q47 case to retained evidence and its actual scope.
**The complete original matrix remains open:** Q33's full controlling-branch
replacement/current-selection retirement is not qualified. Existing history
projection excludes superseded fan-out selection and reports Partial/Unknown
coverage; it does not implement generic branch retirement. This limitation is
separate from the selected profile's fixed immutable selections and from Q47's
platform gate. The final suites preserve the retained regression tests.

## Local verification

- Native: **1,935 passed**, eleven project/framework receipts; **701 SQLite cases
  per framework**, formatting, seven packages/fourteen matching source-build DLLs,
  isolated seven-package closure and unchanged precompiled legacy consumer.
- Fuwen: **1,712 passed**, eight project/framework receipts; core 314, compiler
  251, Baize 94 and adapter 197 per framework. Formatting and independent canonical
  JSON vector pass. Four packages/eight DLLs match the exact source build.
- Package-only Marang/Review consumers: **26 passed** (7 + 6 per framework), exact
  candidate archive/cache equality and no source project references.
- The precompiled public Fuwen preview.11 consumer runs unchanged against the
  candidate runtime on a fresh schema-9 database. Its original adapter/native
  preview.14 schema-4 database is rejected unchanged by both preview.15 and the
  candidate; this is not claimed as a schema-4 upgrade. Native supported schema
  5–9 migration and older-worker rejection pass separately.
- Candidate preparation rejects wrong source/version, missing/duplicate packages,
  unproven changed hashes and a corrupt final archive before feed writes. CI source
  rebuilds have separate hashes and provenance tied to the immutable original
  pin. Only the seven shipped native projects are packed.

All generated local artifacts, package caches and project outputs are on
`D:/temp/activity-groups`, with logical workspace junctions. Red and staging runs
are retained separately and excluded from passing totals.

## Remote qualification

[Native CI 39342046787](https://github.com/jenolaszlo-sketch/penghou-zhinu/actions/runs/39342046787)
passed both OS jobs at harness commit `39cf843e56a0530434a5f072095d5c97fdc164e2`:
**1,935 tests per OS**, formatting, all seven packages and both compatibility probes.
[Fuwen CI 39342986233](https://github.com/jenolaszlo-sketch/penghou-fuwen/actions/runs/39342986233)
passed both producers and both consumers at harness commit
`c851ac68c7047b5fec3476ab87d4ef4fb20a86e6`: **1,712 regression tests and 26
package-only consumer tests per OS**, plus candidate-preparation rejection checks,
native closure/legacy probes and precompiled Fuwen compatibility.

The harness commits have exactly the same `src` Git trees as the frozen local
runtime commits above. CI package repository metadata identifies the actual CI
producer commit; it is not substituted into the immutable original archive pin.
Both consumer jobs download the same Ubuntu producer closure of four Fuwen and
seven native packages. The final audit compares all eleven archive hashes across
both consumers and against that producer, preserving separate source-rebuild
manifests. All receipts and package hashes are retained in the JSON ledger.

Both default consumer configurations also passed locally with an empty cache.
Their raw resolved assets, cache/archive comparisons and 26 passing executions
are retained separately from the earlier explicit-config run. The historical
evidence audit checked **339 recorded artifact hashes**, all matching.

## Second pass and remaining work

The [second correctness pass](second-pass-review.md) records the item-progress
fix, the reproduced empty-body admission defect, cross-platform TEMP and formatter
corrections, bounded wait tests and contradictory status-document cleanup. It
also audits immutable pins, identity/parentage, claim fencing, recovery, projection,
mapper and package boundaries. No additional confirmed runtime defect remained
in the selected profile after those corrections.

Generic branch-selection retirement (full Q33), broader dynamic authoring,
richer macro interfaces/host-resolved role parameters,
public group controls, downstream host integration and package publication remain
separate work. Original full FM-0/FM-1 roadmap boxes retain their broader scope;
this record qualifies the selected executable handoff rather than all future
language features. No package is published and no branch is merged by this task.
