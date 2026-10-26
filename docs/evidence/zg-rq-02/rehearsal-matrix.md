# Rehearsal matrix R01-R18

Status: **executed; the final hosted Zhinu run and both Fuwen attempts are
recorded below.** No scenario created a tag, published a package, or merged a
canonical branch. Evidence classes: [LOCAL] local fault-injection or fixture
execution against the production verifier; [LIVE-API] read-only queries against
real GitHub-hosted historical runs; [HOSTED] GitHub-hosted workflow execution;
[INSPECT] source/configuration inspection; [REUSE] predecessor ZG-RQ-01
evidence.

## Scenario matrix

| ID | Fault / condition | Expected | Observed | Evidence |
| --- | --- | --- | --- | --- |
| R01 | Exact commit, successful qualification, matching artifact set | PASS | PASS | [LOCAL] Zhinu `local/artifact-matrix.json` R01; [HOSTED] final Zhinu rehearsal `40314794489` verification (run outcome recorded in "Hosted runs") |
| R02 | Qualification absent; old-format run without the Stage-A artifact; expired artifact | REFUSED | REFUSED | [LOCAL] `local/api-offline-03.json` (`absent`, `expired`, `artifact-absent`); [LIVE-API] `local/live-api-negatives.json` R02-no-runs (`d4968ea`) and R02-old-format (`f0edf17`, run `40190759599`) |
| R03 | Only pending/in-progress qualification | REFUSED | REFUSED | [LOCAL] `local/api-offline-03.json` (`pending`) |
| R04 | Only failed qualification | REFUSED | REFUSED | [LOCAL] `local/api-offline-03.json` (`failed`); [LIVE-API] `18c0624`, run `40159908313`; [HOSTED] Zhinu rehearsal offline fixture `R04-failed-qualification` |
| R05 | Only cancelled/skipped qualification | REFUSED | REFUSED | [LOCAL] `local/api-offline-03.json` (`cancelled`); [LIVE-API] `ca0c466`, run `39347627636` |
| R06 | Qualified commit differs from publication commit | REFUSED | REFUSED | [LOCAL] `local/artifact-matrix.json` R06; offline API `wrong-source`; [HOSTED] Zhinu rehearsal R06 |
| R07 | Artifact SHA-256 differs | REFUSED | REFUSED | [LOCAL] `local/artifact-matrix.json` R07 and R07-incorrect-hash |
| R08 | Required package missing | REFUSED | REFUSED | [LOCAL] `local/artifact-matrix.json` R08 |
| R09 | Unexpected package included | REFUSED | REFUSED | [LOCAL] `local/artifact-matrix.json` R09; Fuwen `feed-rehearsal-02.json` R09-extra-package |
| R10 | Version or package identity differs | REFUSED | REFUSED | [LOCAL] `local/artifact-matrix.json` R10 and R10-package-identity |
| R11 | Dependency constraint differs from the accepted pin | REFUSED | REFUSED | [LOCAL] `local/artifact-matrix.json` R11; Fuwen `local/feed-r11.json` (native core `[1.0.0]` vs `[0.1.0-preview.2]`) |
| R12 | Manifest malformed, missing, wrong profile, or path-traversal | REFUSED | REFUSED | [LOCAL] `local/artifact-matrix.json` R12, R12-missing-manifest, R12-wrong-profile, R12-path-traversal |
| R13 | Different workflow or repository substituted | REFUSED | REFUSED | [LOCAL] `local/api-offline-03.json` (`unrelated-workflow`, `wrong-repository`); [LIVE-API] `local/live-api-negatives.json` R13-wrong-workflow; [HOSTED] Zhinu rehearsal R13-wrong-repository |
| R14 | Manifest run id or attempt differs from the API-verified run | REFUSED | REFUSED | [LOCAL] `local/artifact-matrix.json` R14; offline API `stale-attempt` |
| R15 | Native feed absent, package missing, wrong version, resolved commit mismatch, invalid pin, corrupt bytes | REFUSED | REFUSED | [LOCAL] Fuwen `local/feed-rehearsal-02.json` R15b-R15g (R15a valid feed PASS) |
| R16 | Empty mapped feed with isolated cache; no public-feed fallback | REFUSED | REFUSED | [LOCAL] Fuwen `local/feed-rehearsal-02.json` R16 (`NU1101`; `nuget.org` not considered) |
| R17 | Publication environment approval gate | ENFORCED (pending -> explicit approval -> no credentials) | ENFORCED | [HOSTED] Zhinu runs `40303575468`, `40308720683`, and final `40314794489`; Fuwen `40308636700` attempt 2; environment config in `rg-02-environment.md` |
| R18 | Valid set plus fresh-cache restore | RELEASE-ELIGIBLE (NO PUSH) | RELEASE-ELIGIBLE (NO PUSH) | [LOCAL] Zhinu `local/artifact-matrix.json` R18; Fuwen `local/feed-rehearsal-02.json` R18; [HOSTED] Fuwen ubuntu validation in `40308636700` |

## Hosted runs

| Run | Repo / ref | Outcome | What it proves |
| --- | --- | --- | --- |
| `40303575468` | Zhinu `codex/zg-rq-02-rehearsal` @ `ced5133` | SUCCESS after owner approval | Verification + environment checkpoint; no push |
| `40308720683` | Zhinu `codex/zg-rq-02-rehearsal` @ `908555d` | SUCCESS after owner approval | Verification, 17-scenario offline matrix, offline API/tag fixtures, credential-free handoff, environment checkpoint |
| `40314794489` | Zhinu `codex/zg-rq-02-rehearsal` @ `009c7f5` | SUCCESS after owner approval | Final run at the corrected implementation commit: API verification of CI run `40311550947`, offline matrix, offline API/tag fixtures, credential-free handoff, environment checkpoint; no push. Raw records in `local/hosted-rehearsal-40314794489*` |
| `40302339008` | Fuwen rehearsal @ `bf993ee` | FAILURE (preserved) | Hosted fixture path defect; native preparation and feed checks had passed |
| `40302665778` | Fuwen rehearsal @ `f7245b0` | FAILURE (preserved) | Hosted formatting gate at the diagnostic initializer |
| `40303275879` | Fuwen rehearsal @ `5ec2479` | FAILURE (preserved) | Workflow-contract test pinned the old single-file publisher |
| `40308636700` attempt 1 | Fuwen rehearsal @ `6565822` | FAILURE (preserved) | REL-SQLITE-01 recurrence on windows-latest; handoff and checkpoint skipped (fail-closed); logs and metadata in `local/hosted-rehearsal-40308636700-*` |
| `40308636700` attempt 2 | Fuwen rehearsal @ `6565822` | SUCCESS after owner approval | Same commit passed on rerun (windows 226/226 Zhinu-integration tests green); handoff re-verified all archives; environment checkpoint echoed only; no push. Does not close REL-SQLITE-01 |

## Fail-closed and no-push properties

- Offline verifiers throw before any download or push; the live-API negatives
  exit 1 before downloading an artifact. The rehearsal workflows contain no
  `dotnet nuget push`, no `NuGet/login`, and no `id-token: write`.
- Zhinu's `handoff` job downloads the verified set and re-runs the publisher's
  final artifact verifier with no credentials; the `authorization-probe` job
  runs only after that and only echoes a message.
- Fuwen's `handoff` job re-runs `ReleasePackageSet.ps1 -Mode Verify` with no
  credentials; the `authorization-probe` job is the only environment-gated job.
- The only push steps in either repository are in `publish.yml`'s final job,
  after verification/validation, after `needs`, and behind `environment: nuget`.
- `eng/test_workflow_boundaries.py` (both repositories) asserts the absence of
  unauthorized triggers, ambient OIDC, and push steps outside the protected
  job. It is executed **locally** (raw outputs
  `local/zhinu-workflow-boundaries.json`,
  `local/fuwen-workflow-boundaries.json`, classified "STATIC WORKFLOW
  MUTATION; NOT HOSTED ENFORCEMENT"); the hosted no-push/no-OIDC properties
  rest on workflow inspection and the hosted run records, not on this test.
- No tag was created and no package was published at any point.

## Preserved failures

All failed attempts retain distinct run IDs or fixture directories; nothing was
overwritten. The invalid first Fuwen feed matrix, the two offline API-mock
attempts, the three pre-fix Fuwen hosted runs, the REL-SQLITE-01 hosted
recurrence (attempt 1) with its passing rerun (attempt 2), and the initial
naive artifact-comparison observation are recorded in `observations.md`,
`known-findings.md` and the raw files under `local/`.
