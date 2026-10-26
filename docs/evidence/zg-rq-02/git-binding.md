# Git binding

Recorded 2026-10-26 from the live worktrees. Full SHAs/trees; no abbreviated
identifiers are used as evidence bindings.

## Accepted baselines (from ZG-RQ-01)

| Repository | Baseline commit | Baseline tree |
| --- | --- | --- |
| Penghou.Zhinu | `f0edf1714013106bdbce090179342ccb029eaf83` | `6158fffa4343491ff0ed0e23fe4846598ac5654e` |
| Penghou.Fuwen | `8c7da4cfbc882804d801fe0e8b073733e4ee563c` | `9d3f84b1fe8ac083084f8750001f3ab80168128e` |
| Penghou.Hufu | `a3e43edda3a608e2c96f39359cb0ba648234c5da` | `1a4e1edcfccbd6fdc223a8707213bea6d41cdf3e` |
| Penghou (contracts) | `2c6d6449bd18f14a5e7664a8774b6e4ff154539e` | `9349ad15d7f8e9717bb877141455f9bee0f1c9ef` |

ZG-RQ-01 evidence branch: `codex/zg-rq-01` at
`d272b26` (predecessor; not merged, not overwritten).

## Implementation branches and worktrees

| Repository | Branch | Worktree | HEAD commit | Tree | Parent | Worktree state | Pushed |
| --- | --- | --- | --- | --- | --- | --- | --- |
| Penghou.Zhinu | `codex/zg-rq-02-zhinu` | sibling `zg-rq-02-zhinu` | `009c7f5876a2dd6133f2ebc80e0806790e6e9aba` | `202bdf3c3ac4daca3f8e314f4ce9c70a4d44ba02` | `a4bdfa503f88fee8e1416e0ba769a3a5b67ed149` | evidence files committed in this packet's commit(s); otherwise clean | `origin/codex/zg-rq-02-zhinu` |
| Penghou.Zhinu | `codex/zg-rq-02-rehearsal` (dispatch ref) | same commit | `009c7f5876a2dd6133f2ebc80e0806790e6e9aba` | — | — | — | `origin/codex/zg-rq-02-rehearsal` |
| Penghou.Fuwen | `codex/zg-rq-02-fuwen` | sibling `zg-rq-02-fuwen` | `65658227151cfbcc69fde752832e4b6b0691d861` | `7ea3abc322dd55dc2007d7ecf2e95ffe09322e9a` | `5ec24791b6107bab2605ecfe6c45436cd3b74aaa` | evidence files committed in this packet's commit; otherwise clean | `origin/codex/zg-rq-02-fuwen` |
| Penghou.Fuwen | `codex/zg-rq-02-rehearsal` (dispatch ref) | same commit | `65658227151cfbcc69fde752832e4b6b0691d861` | — | — | — | `origin/codex/zg-rq-02-rehearsal` |

Pinned native source worktree (read-only staging input):
`zg-rq-02-native` at `bf33c05cf2390af6924bbe3761e09f9e377c7a97` (detached).

## Branch history

Zhinu (`d272b26..009c7f5`):

```text
009c7f5 Correct rehearsal scenario map: hosted API fixtures are offline; live-API negatives are separate evidence
a4bdfa5 Describe rehearsal coverage accurately: hosted fixtures are offline; live-API negatives are recorded local evidence
908555d Exercise the final immutable artifact handoff in the hosted rehearsal
ced5133 Bind qualified release artifacts and staged native feed; add fail-closed rehearsals
b21c59b Preserve interrupted ZG-RQ-02 implementation and independently recovered state
```

Fuwen (`8c7da4c..6565822`):

```text
6565822 Align release-workflow contract test with the reusable qualification architecture
5ec2479 Align stress diagnostic initializer formatting with hosted SDK
f7245b0 Use portable native-feed fixture paths and retain hosted failure details
bf993ee Bind qualified release artifacts and staged native feed; add fail-closed rehearsals
101643a Retain SQLite stress diagnostics without changing production or assertions
0fd7e72 Preserve interrupted ZG-RQ-02 implementation and independently recovered state
```

The recovery commits (`b21c59b`, `0fd7e72`) preserve the interrupted
implementation state and the independently recovered evidence under
`docs/evidence/zg-rq-02/recovery/`; they do not alter production files.

## Scope proof (production files unchanged)

`scope-check.json` (both repositories) records
`git diff --name-status <baseline> HEAD -- src Directory.Build.props
NuGet.config` returning empty. Independently re-verified from the worktrees on
2026-10-26: both diffs are empty. Every changed path is a workflow, an
`eng/` release-verification/stress script, a test, release documentation or
evidence:

- Zhinu: `.github/workflows/{ci,publish,release-rehearsal}.yml`,
  `eng/{New-ReleaseQualificationManifest,Verify-QualificationRun,
  Verify-ReleaseArtifacts,Verify-ReleaseRef,Invoke-ReleaseRehearsal,
  Test-QualificationBoundary}.ps1`, `eng/test_workflow_boundaries.py`,
  `ROADMAP.md`, `docs/releasing.md`, `docs/evidence/**`.
- Fuwen: `.github/workflows/{publish,release-qualification,
  release-rehearsal}.yml`, `eng/{Verify-NativeFeed,ReleasePackageSet,
  Invoke-ReleaseRehearsal}.ps1`, `eng/{sqlite_stress.py,
  test_workflow_boundaries.py}`,
  `tests/Penghou.Fuwen.Compiler.Tests/ReleaseWorkflowContractTests.cs`,
  `tests/Penghou.Fuwen.Zhinu.Tests/FuwenZhinuSelectionTests.cs` (env-gated
  diagnostic capture only; no assertion changes), `docs/release-checklist.md`,
  `docs/evidence/**`.

## Remote state

Pushed branches (verified via `git ls-remote --heads origin`):
`codex/zg-rq-02-zhinu`, `codex/zg-rq-02-rehearsal` (Zhinu);
`codex/zg-rq-02-fuwen`, `codex/zg-rq-02-rehearsal` (Fuwen). Canonical `main`
was not modified, merged or force-updated. No tags were created.

## Evidence commits

- Zhinu evidence packet commit: `18af51bab1ad9a0ad9a588b155463479ef32dd33`
  (docs-only; parent `009c7f5876a2dd6133f2ebc80e0806790e6e9aba`)
- Fuwen evidence packet commit: `8a18a3f9b18da368dc973c0a6a733c1e34fa30d3`
  (docs-only; parent `65658227151cfbcc69fde752832e4b6b0691d861`)

These documentation-only commits follow the implementation commits above; the
follow-up binding commit that fills these values is the branch tip and is
visible in `git log`. Neither commit touches `src/`, tests, workflows or
scripts.
