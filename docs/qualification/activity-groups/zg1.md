# ZG-1 qualification

Date: 2026-10-15. Result: **passed locally on Windows, .NET 8 and .NET 10**.
Branch: `codex/zhinu-activity-groups`, based on `b7f1009`.
Contract: [immutable group manifests](../../activity-groups-zg1.md).
Machine-readable results and exact package/source hashes: [zg1.json](zg1.json).

## Results

| Check | Result |
| --- | --- |
| Full solution tests | 1,261 passed, 0 failed, 0 skipped, across 11 test assemblies/TFMs |
| Core tests | 111 per framework; includes 23 group contract/vector cases and 6 legacy capability cases per framework |
| SQLite regression tests | 474 per framework, including existing recovery/migration/fencing suites |
| Repository format/analyzer verification | Passed |
| Candidate packing | Seven package pairs produced at existing version 0.2.0-preview.4 |
| Isolated package closure | Passed on net8.0/net10.0, exact candidate packages, fresh cache, no project references or forbidden Hufu dependency |
| Legacy compiled consumer | Passed on both frameworks without recompiling the old consumer |
| Retained database compatibility | Published schema-5 data upgraded to current schema 7 with retained rows unchanged; old worker rejected afterward on both frameworks |
| Final probe-script diagnostic parameterization | Parsed successfully and matched both captured old-worker rejection outputs |

No group runtime, occurrence, persistence or UI capability is claimed. These
contract tests cover the static portions of Q01-Q09/Q24/Q26; the package probes
cover Q25 compatibility. Actual grouped-run versions of those cases and Q10-Q47
runtime behavior remain with their later implementation stages.

The first legacy-consumer attempt exposed the preceding authority-binding change's
missing seven-argument engine signatures. This checkpoint restores forwarding
overloads and the original abstract capability slots. Authority-aware interface
defaults delegate null bindings to legacy implementations and reject non-null
bindings rather than discard authority identity. The probe's stale schema-6
expectation was replaced with explicit ExpectedCandidateSchemaVersion=7.
The passing results above are from the repaired candidate, not the failed attempt.

## Reproduce

Run from the worktree root with the repository's .NET SDK/runtimes installed:

```powershell
dotnet restore Penghou.Zhinu.slnx
dotnet test Penghou.Zhinu.slnx -c Release --no-restore --logger trx --results-directory artifacts/zg1-qualified-tests
dotnet format Penghou.Zhinu.slnx --verify-no-changes --no-restore
dotnet pack Penghou.Zhinu.slnx -c Release --no-build --no-restore -o artifacts/zg1-qualified-packages
./eng/Verify-WorkflowPackageClosure.ps1 -ArtifactsDirectory artifacts/zg1-qualified-packages -PackageVersion 0.2.0-preview.4 -OutputDirectory artifacts/zg1-qualified-closure
./eng/Verify-LegacyWorkflowConsumer.ps1 -ArtifactsDirectory artifacts/zg1-qualified-packages -PackageVersion 0.2.0-preview.4 -ExpectedCandidateSchemaVersion 7 -OutputDirectory artifacts/zg1-qualified-legacy
```

TRX, build/probe logs, candidate packages and full generated consumers remain in
the ignored `artifacts/zg1-*` directories. The checked-in JSON retains aggregate
results, source hashes and the probes' detailed qualification records/package
hashes. Local candidates were built from the verified worktree before commit;
source-content hashes identify the code tested independently of package Git
metadata. No publication, remote CI or Linux execution is claimed.

Luna produced the contract tests and independent hand-authored digest vectors,
reviewed the implementation bounds/immutability, and checked the compatibility
dispatch. The primary agent integrated, corrected and executed the final checks.

## Next checkpoint

Implement ZG-2 coordinates and a narrow ZG-3 durable vertical, with optional
attachment APIs introduced only when manifest installation/membership can be
persisted and validated at every start/resume/child/fork boundary. Recheck the
next free schema number and preserve the existing schema-7 authority binding.
