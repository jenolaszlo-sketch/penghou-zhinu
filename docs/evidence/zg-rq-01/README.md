# ZG-RQ-01: Zhinu / Fuwen paired release readiness qualification

Status: **REMEDIATION REQUIRED** (narrowly scoped to release automation, gate Q6).
Technical qualification gates Q1-Q5 pass with the evidence recorded in this
packet; gate Q6 found the release-automation defects enumerated below and in
[release-gates.md](release-gates.md); gate Q7 dispositions are recorded in
[known-findings.md](known-findings.md). Publication is not authorized by this
qualification and remains a separate owner decision.

## Candidate bindings

All bindings recorded as full Git commit and tree SHAs on 2026-10-26.
Worktrees clean at the time of binding.

| Repository | Branch | Commit | Tree |
| --- | --- | --- | --- |
| Penghou.Zhinu | `main` | `f0edf1714013106bdbce090179342ccb029eaf83` | `6158fffa4343491ff0ed0e23fe4846598ac5654e` |
| Penghou.Fuwen | `main` | `8c7da4cfbc882804d801fe0e8b073733e4ee563c` | `9d3f84b1fe8ac083084f8750001f3ab80168128e` |
| Penghou.Hufu | `main` | `a3e43edda3a608e2c96f39359cb0ba648234c5da` | `1a4e1edcfccbd6fdc223a8707213bea6d41cdf3e` |
| Penghou (contracts) | `main` | `2c6d6449bd18f14a5e7664a8774b6e4ff154539e` | `9349ad15d7f8e9717bb877141455f9bee0f1c9ef` |

Candidate packages: Zhinu `0.2.0-preview.10` (seven packages, SQLite schema 10,
additive migration chain 5-6-7-8-9-10), Fuwen `0.1.0-preview.20` (four
packages). Neutral contract: published stable `Penghou.Workflow.Abstractions`
1.0.0 exists; both candidates pin the explicitly qualified exact
`[0.1.0-preview.2]`. Runtime matrix: .NET 8.0 and 10.0 (ASP.NET Core package
targets .NET 10 only); release-policy OS matrix: Windows and Ubuntu.

## Native baseline equivalence

The Fuwen release packet pins native runtime/package source
`bf33c05cf2390af6924bbe3761e09f9e377c7a97` (preview.10, schema 10) with its
original archive hashes. `git diff bf33c05 f0edf17` over `src/` and `tests/` is
**empty**; the delta is documentation plus one qualification script
(`eng/Verify-BranchReplacementPackages.ps1`). All bf33c05-bound evidence
therefore applies to the bound candidate without re-running, and every
re-execution in this packet was performed at `f0edf17` itself.

## Coverage matrix

| Gate | Verdict | Evidence |
| --- | --- | --- |
| Q1 persistence and schema compatibility | PASS | [compatibility.md](compatibility.md) |
| Q2 activity-groups durability and recovery | PASS (selected profiles) | [activity-groups-recovery.md](activity-groups-recovery.md) |
| Q3 legacy binary and API compatibility | PASS | [compatibility.md](compatibility.md) |
| Q4 package closure and local artifact qualification | PASS (local; public archive step pending publication) | [package-qualification.md](package-qualification.md) |
| Q5 Zhinu/Fuwen paired compatibility (local pair) | PASS (local pair; public pair pending publication) | [paired-qualification.md](paired-qualification.md) |
| Q6 release automation and publication safety | REMEDIATION REQUIRED (findings RG-01, RG-02, RG-03) | [release-gates.md](release-gates.md) |
| Q7 known failure disposition | DISPOSITIONED (REL-SQLITE-01 open, recurrence response due) | [known-findings.md](known-findings.md) |

## Verdict

**REMEDIATION REQUIRED.** A necessary release gate is currently unenforceable
or unrunnable as specified:

- **RG-01:** the Zhinu publish workflow performs no automated verification
  that CI qualified the exact commit being published; the gate is a documented
  procedural operator check only.
- **RG-03:** the Fuwen tag/publish workflow cannot currently run at all: root
  `NuGet.config` maps all seven native package IDs exclusively to
  `artifacts/zhinu-candidate`, which the workflow never prepares, and the
  native packages are absent from the public feed at the recorded observation.

No technical defect was found in the candidates themselves: gates Q1-Q5 pass,
and the Q6 findings are confined to release automation. See
[acceptance-recommendation.md](acceptance-recommendation.md) for the narrowly
scoped remediation and the reissue procedure.
