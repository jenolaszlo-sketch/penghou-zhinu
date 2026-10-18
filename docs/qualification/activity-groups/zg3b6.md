# ZG-3b.6 qualification: consolidated native durability

Date: 2026-10-18. Status: **passed for the selected native profiles**.
Starting checkpoint: `bbd98d5bd330e360d64d8b77d0205a6b6828a136`.
Consolidation test/tooling source: `0684ee515873efc5a9b52701f988d6ba977d3609`.
Runtime/package source remains `3ca11057c5c43463bc9c82304058d68e60b3433e`.
The commit containing this record adds evidence/docs only. Machine-readable
checks and rejection receipts are in [zg3b6.json](zg3b6.json). Read the
[capability matrix and ZG-4 entry contract](../../activity-groups-zg3b6.md).

> **Nested repetition required no runtime algorithm, schema, or identity redesign.**

## Gates and what ran

| Gate | Result |
| --- | --- |
| Historical source integrity | 77 source-hash entries and 36 retained-source entries verified against their recorded Git commits |
| Full-suite evidence integrity | All 55 retained TRX files match SHA-256 and passed counters; five individual 11-file profile qualifications reconcile |
| Package evidence integrity | All 35 retained nupkg hashes and repository commits match the five exact sources |
| Package/binary/schema gates | Each profile retains passing .NET 8/10 consumers, legacy DLL substitution, schema-5→9 migration and old-worker rejection with four retained step rows |
| Current runtime boundary | All `src/` files and both package-consumer scripts match `3ca1105`; existing tests are unchanged |
| New focused tests | **8 passed**, four cases on each framework; zero failures/skips |
| Rejection evidence | Existing negative cases located in the latest full-suite TRX; eight fresh detailed receipts cover the two newly tested guard paths |
| Audit negative controls | Five copied-ledger corruptions reject at the expected gate and write no passed audit output |
| Formatting | Full solution `dotnet format --verify-no-changes --no-restore` passed |
| Runtime/schema/API | No runtime code, public API, schema or identity encoder changes; schema remains **9** |

The retained complete suite has **1,639 passing executions** on its exact source.
This checkpoint runs **eight additional focused executions**; it does **not**
claim a new 1,647-test full-suite run. No new process-loss workers or package
builds were required. Reuse is supported by unchanged runtime/consumer sources,
unchanged existing tests, artifact hash verification and the retained exact-source
qualification. Assembly build metadata for the new test build does not replace
the identity of the previously packed candidate artifacts.

## Profile ledger

| Profile | Runtime/package source | Historical full-suite total | Positive process receipts | Negative omission cases | Tranche-specific worker starts |
| --- | --- | ---: | ---: | ---: | ---: |
| [Root fan-out](zg3b1.md) | `e4b7f139b93c3934860ed5a3f7aae9225d3f5ba3` | 1,441 | 8 | 0 | 16 |
| [Separate-run forks](zg3b2.md) | `28818cfc12831978a06b2fcd46d258699f0b9204` | 1,499 | 14 | 0 | 28 |
| [Fixed nested repetition](zg3b3.md) | `4d24d6c4e25efc4efdf99b4e3f44f461e227019d` | 1,531 | 14 | 0 | 28 |
| [Explicit evolving epochs](zg3b4.md) | `bfa1bf6c71b5bf4a29fe09a508f9a237b0f4cea6` | 1,571 | 16 | 2 | 36 |
| [Native children](zg3b5.md) | `3ca11057c5c43463bc9c82304058d68e60b3433e` | 1,639 | 16 | 0 | 38 |

Full-suite totals are cumulative and must not be summed as unique coverage.
Tranche-specific historical evidence totals **68 positive recovery receipts**, two
negative omitted-history cases and **146 worker starts**. The child profile uses
seven independent crash/recovery pairs plus one five-worker sequence per
framework; a receipt is therefore not always exactly two workers. These numbers
exclude repeated older workers in later cumulative suites and are not executions
performed by this consolidation audit.

All profiles use schema 9. Group manifest v1 and root/loop occurrence v1 remain
unchanged; native fan-out uses v2. Forks use destination RunId with existing
coordinates. Epochs use stable authored native scope keys. Child IDs retain the
existing parent RunId/invocation/start-revision hash. No new identity scheme was
needed to consolidate these independently qualified profiles.

## Concrete audit findings and coverage added

The audit found two guards without direct integration coverage at their relevant
public boundary. `ActivityGroupCompositionBoundaryTests` closes both:

- Ordinary `StepAsync` with `FanOutGroup` rejects with the exact native guard
  message, for null/root-parent declarations. The completed selector remains;
  there is no ordinary step claim, callback, occurrence, membership or expansion.
- Both engine `ForkAsync` routes reject a genuinely completed grouped fan-out
  source. Tests compare the source run, steps, events, dependencies, manifest,
  occurrences, memberships, expansions, items and closures before/after rejection.
  Run counts do not change and a chosen destination ID remains absent. Existing
  grouped store transaction rejection remains independently tested.

No product defect was found and no runtime guard was changed. Luna independently
audited composition guards and the historical ledgers; root reviewed and
strengthened the new assertions and implemented the reproducible audit.

A documentation boundary was clarified: explicit loop-coordinate parents and
competing typed loop/fan-out coordinates reject. The runtime does not infer
arbitrary C# lexical nesting from calls through an untyped WorkflowContext.
Arbitrary mixed compositions and typed child lowering remain **not qualified**;
they are not described as universally detected or rejected. Grouped
fan-out/fork copying has an explicit run marker and remains an enforced rejection.

## Audit controls

The verifier checks exact Git source bytes, allowing only the corresponding CRLF
Windows checkout representation when a historical ledger used it. It checks
local TRX/package bytes against their recorded hashes and verifies each nupkg's
repository commit. A package version string alone cannot identify these repeated
unpublished `0.2.0-preview.4` candidates.

Five controls copy the committed ledgers into isolated artifact directories and
corrupt only the copied data:

| Mutation | Expected rejection |
| --- | --- |
| Source SHA-256 replaced with zeros | Source hash mismatch |
| Package SHA-256 replaced with zeros | Artifact hash mismatch |
| Recorded test pass count changed | Test receipt mismatch |
| Profile total changed | Invalid profile header |
| Legacy compatibility marked failed | Compatibility gate mismatch |

All five rejected as expected, without writing a passed output file. Their
observed errors are retained in the JSON. Original evidence, tests and packages
were not modified by these controls.

## Reproduction

Use the consolidation source above, the existing exact-source artifact bundle
and a new empty result directory. The verifier is an explicit qualification tool,
not a normal build prerequisite:

```powershell
$env:MSBUILDDISABLENODEREUSE = '1'
$env:DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER = '1'
dotnet test tests/Penghou.Zhinu.Sqlite.Tests/Penghou.Zhinu.Sqlite.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~ActivityGroupCompositionBoundaryTests --logger trx --results-directory artifacts/zg3b6-composition-tests -v minimal
dotnet format Penghou.Zhinu.slnx --verify-no-changes --no-restore
./eng/Verify-ActivityGroupQualification.ps1 -OutputPath artifacts/zg3b6-audit-qualified.json
```

Historical TRX files must remain at their ledger-relative locations, with the
seven packages in each `artifacts/zg3b1-packages` through `zg3b5-packages` directory.
Missing or mismatched local evidence fails the audit; committed summaries alone
cannot manufacture a local artifact verification. Obtain the matching bundle or
requalify the exact profile source and retain fresh evidence. To repeat a negative
control, copy the five JSON ledgers, apply one mutation above, and pass that copy
via `-EvidenceDirectory` with a new `-OutputPath`.

## Exit and remaining limits

ZG-3b is complete for the **bounded selected native profiles**. Arbitrary mixed
composition, implicit retirement/in-place changed-plan replacement, atomic
subtree cancellation, coherent cross-run queries, aggregate state/progress,
Fuwen adapter/DSL and cross-repository qualification remain open as explicitly
listed in the capability matrix. Local Windows evidence does not establish
Linux/remote CI or publication.

Next is **ZG-4 coherent hierarchy queries**, preserving run-local ownership,
direct membership, exact revision history, native child/source links and honest
coverage at one read boundary. Aggregate state/progress remains ZG-5, Fuwen ZG-6
and cross-repository closure ZG-7. No reader/progress API is implemented here.
