# ZG-RQ-02 handoff (sections A-H)

Provisional verdict by the author: **QUALIFIED WITH QUALIFICATIONS** — subject
to the independent review required in section G. This document is **not
self-certification**: the author implemented the remediation and may not sign
it off. Publication remains unauthorized; no tag, no package, no merge.

## A. Identification

| Item | Value |
| --- | --- |
| Milestone | ZG-RQ-02 (Q6 delta remediation of ZG-RQ-01 findings RG-01, RG-02, RG-03; REL-SQLITE-01 stress response in separate work) |
| Predecessor | ZG-RQ-01 `codex/zg-rq-01` @ `d272b26`, verdict REMEDIATION REQUIRED (Q6 only) |
| Zhinu implementation branch | `codex/zg-rq-02-zhinu` @ `009c7f5876a2dd6133f2ebc80e0806790e6e9aba`; tree `202bdf3c3ac4daca3f8e314f4ce9c70a4d44ba02`; parent `a4bdfa503f88fee8e1416e0ba769a3a5b67ed149`; worktree `zg-rq-02-zhinu` |
| Zhinu history (newest first) | `009c7f5` then `a4bdfa5` then `908555d` then `ced5133` then `b21c59b` then `d272b26` |
| Fuwen implementation branch | `codex/zg-rq-02-fuwen` @ `65658227151cfbcc69fde752832e4b6b0691d861`; tree `7ea3abc322dd55dc2007d7ecf2e95ffe09322e9a`; parent `5ec24791b6107bab2605ecfe6c45436cd3b74aaa`; worktree `zg-rq-02-fuwen` |
| Fuwen history (newest first) | `6565822` then `5ec2479` then `f7245b0` then `bf993ee` then `101643a` then `0fd7e72` then `8c7da4c` |
| Rehearsal dispatch refs | Zhinu `codex/zg-rq-02-rehearsal` @ `009c7f5`; Fuwen `codex/zg-rq-02-rehearsal` @ `6565822` |
| Pinned native source worktree | `zg-rq-02-native` @ `bf33c05cf2390af6924bbe3761e09f9e377c7a97` (detached, read-only staging input) |
| Evidence commits | Zhinu packet `18af51bab1ad9a0ad9a588b155463479ef32dd33` (docs-only); Fuwen packet `8a18a3f9b18da368dc973c0a6a733c1e34fa30d3` (docs-only); the follow-up binding commits are visible in `git log` and `git-binding.md` |
| Unchanged | `main` not merged or force-updated; no tags; no publication; production source/APIs/package versions unchanged (`scope-check.json`) |

## B. What was implemented

**RG-01 (Zhinu exact-commit publication gating).** Two-stage release:

- `ci.yml` (ubuntu producer) generates `release-qualification-manifest.json`
  (`eng/New-ReleaseQualificationManifest.ps1`) binding commit, run id/attempt,
  conclusion, package identities, SHA-256 of every nupkg/snupkg, target
  frameworks and dependency pins, and uploads
  `release-qualification-ubuntu-latest`.
- `publish.yml` `verify` resolves the exact publication ref
  (`eng/Verify-ReleaseRef.ps1`: manual `workflow_dispatch` on `main`, or tag
  `v<checked-in version>` resolving to the commit), then locates the
  qualification run **through the authenticated GitHub API**
  (`eng/Verify-QualificationRun.ps1`: only `push`/`workflow_dispatch` runs of the
  canonical `ci.yml` path, same repository, completed/successful, both platform
  jobs successful, non-expired artifact whose manifest matches run/attempt/
  source), downloads that artifact set, and verifies it offline
  (`eng/Verify-ReleaseArtifacts.ps1`: manifest profile/schema, identity, version,
  canonical filenames, duplicate/unsafe ZIP members, repository metadata, hashes,
  exact package/symbol set, dependency pins).
- `publish.yml` `publish` runs only after `verify` succeeds and the `nuget`
  environment approves; it is the only `id-token: write` job, re-downloads the
  verified artifact set, re-runs the offline verifier from a fresh checkout, and
  only then runs `NuGet/login` — it never packs. Fail-closed on every deviation.

**RG-03 (Fuwen native-feed prerequisite).** A reusable
`.github/workflows/release-qualification.yml` stages the pinned native feed
before any restore: reads `docs/qualification/activity-groups/zhinu-candidate.json`
(commit `bf33c05`, version `0.2.0-preview.10`), checks out exactly that commit,
rebuilds the seven packages (`eng/Prepare-ZhinuSourceCandidate.ps1`), runs
rejection boundaries (`eng/Test-ZhinuCandidatePreparation.ps1`), verifies the
feed (`eng/Verify-NativeFeed.ps1`: exact seven IDs, version, source commit,
per-run rebuild hashes, dependency pin `Penghou.Workflow.Abstractions`
`[0.1.0-preview.2]`), and uploads `native-candidate-feed`. `validate` consumes
that artifact with an empty package cache; `NuGet.config` maps the seven native
IDs exclusively to `zhinu-candidate`, so restore cannot fall back to nuget.org
(proved by the R16 `NU1101` message). `publish` consumes only the ubuntu package
set, re-verifies with `ReleasePackageSet.ps1 -Mode Verify`, then requests OIDC
behind the environment. No version range was widened and no public feed was
referenced.

**RG-02 (environment protection).** Both `nuget` environments now have
`required_reviewers` (owner `jenolaszlo-sketch`, id `312601944`),
`can_admins_bypass: false`, `prevent_self_review: false` (owner decision; see
limitation in section F). Applied through the GitHub API and verified live: runs
paused at pending deployment, the owner approved explicitly, and the checkpoint
jobs then ran only their echo step with no credentials. Details:
`rg-02-environment.md`.

## C. Evidence index and classification

| Document | Class labels |
| --- | --- |
| `README.md` | [INSPECT] summary |
| `git-binding.md` | [INSPECT] commits/trees/parents/remote state |
| `rg-01-zhinu.md` | [INSPECT] + [LOCAL] + [HOSTED] + [LIVE-API] |
| `rg-02-environment.md` | [ACTUAL GITHUB CONFIG] + [HOSTED] live gate |
| `rg-03-fuwen.md` | [INSPECT] + [LOCAL] + [HOSTED] |
| `artifact-equivalence.md` | [SOURCE INSPECTION] + [ARTIFACT INSPECTION] |
| `rehearsal-matrix.md` | [LOCAL] + [LIVE-API] + [HOSTED] R01-R18 |
| `permissions-review.md` | [ACTUAL GITHUB CONFIG] + [INSPECT] |
| `known-findings.md` | [INSPECT] + [REUSE ZG-RQ-01] |
| `observations.md` | [LOCAL] preserved failures/method notes |
| `security-review.md` | [INSPECT] + [ACTUAL GITHUB CONFIG] |
| `scope-check.json` | [SOURCE INSPECTION] production delta empty |
| `local/` | raw API captures, matrices, run metadata, preserved logs |
| `../Penghou.Fuwen/docs/evidence/zg-rq-02/` | Fuwen packet (feed matrix, R11, native equivalence, stress qualification, hosted attempt logs) |

Source equivalence supporting Q1-Q5 inheritance: `git diff f0edf17 bf33c05 --
src tests` is **empty**; the full `f0edf17..bf33c05` delta is only
documentation plus `eng/Verify-BranchReplacementPackages.ps1`. Production delta
on both implementation branches vs baseline (`src`, `Directory.Build.props`,
`NuGet.config`) is empty in `scope-check.json`.

Artifact properties: the hosted Stage-A set is **semantically equivalent** to
the recorded ZG-RQ-01 candidate set — 14/14 archives with identical nuspec
dependency pins (only the expected repository branch/commit binding differs),
7/7 XML docs identical after line-ending normalization, 14/14 assemblies with
identical types, references and per-method IL. Archive bytes, MVIDs, PDB
checksums and ZIP metadata differ by build environment; this is not a
byte-reproducibility claim (`artifact-equivalence.md`,
`local/artifact-equivalence.json`).

## D. Rehearsal results

Full matrix: `rehearsal-matrix.md`. Summary of hosted evidence:

| Run | Outcome |
| --- | --- |
| Zhinu CI `40303504133` @ `908555d` | SUCCESS (Stage-A producer) |
| Zhinu CI `40310265035` @ `a4bdfa5` | SUCCESS |
| Zhinu CI `40311550947` @ `009c7f5` | SUCCESS (final implementation commit) |
| Zhinu rehearsal `40308720683` @ `908555d` | SUCCESS after approval: verify + 17-scenario offline matrix + 19 offline API/ref fixtures + credential-free handoff + checkpoint |
| Zhinu rehearsal `40314794489` @ `009c7f5` | SUCCESS after approval: same path at the final commit, qualifying CI `40311550947` |
| Fuwen rehearsal `40308636700` attempt 1 @ `6565822` | FAILURE (preserved): REL-SQLITE-01 recurrence on windows-latest; handoff and checkpoint skipped (fail-closed) |
| Fuwen rehearsal `40308636700` attempt 2 @ `6565822` | SUCCESS after approval: feed staged/verified, both platforms validated (windows 226/226 Zhinu-integration tests), handoff re-verified all archives, checkpoint echoed only |

Live-API negatives (read-only, against real historical runs, recorded in
`local/live-api-negatives.json`): R02 no-runs (`d4968ea`), R02 old-format
(`f0edf17` / run `40190759599`), R04 failed (`18c0624` / run `40159908313`),
R05 cancelled (`ca0c466` / run `39347627636`), R13 wrong-workflow
(`publish.yml` at `18c0624`) — all exit 1 with the intended messages.

## E. Preserved failures and method observations

Recorded in `observations.md` and the raw files under `local/`; nothing was
overwritten or rewritten:

1. First Fuwen feed matrix (`feed-rehearsal-01.json`) invalid due to a
   strict-mode harness defect; corrected harness produced the valid
   `feed-rehearsal-02.json`.
2. Two failed offline API-mock harness attempts (`api-offline-01/02`); the
   completed fixture produced `api-offline-03.json`. The hosted rehearsal's API
   step is **offline fixtures**, not live-API calls; the live-API negatives are
   separate read-only local evidence. Workflow/script headers were corrected
   (`a4bdfa5`, `009c7f5`).
3. Three pre-fix Fuwen hosted failures (`40302339008`, `40302665778`,
   `40303275879`), each fixed and re-run with distinct runs preserved.
4. Fuwen hosted rehearsal attempt 1 reproduced REL-SQLITE-01; the log and
   metadata were copied into `local/` **before** the rerun. Attempt 2 passed;
   both outcomes are retained and the finding remains OPEN.
5. The first Zhinu baseline-vs-Stage-A artifact comparison (naive byte/hash
   comparison) reported differences caused by ZIP metadata, MVID/PDB values and
   XML line endings; the corrected method established semantic equivalence.
6. Targeted R11 native dependency-pin fixture was executed against the
   re-paired current feed/manifest and refused with the intended message
   (`../Penghou.Fuwen/docs/evidence/zg-rq-02/local/feed-r11.json`).

## F. Open items and limits

- **REL-SQLITE-01 — OPEN.** `SQLite Error 10: 'disk I/O error'` at
  `lease.recover` on Windows process-loss recovery. Occurrences: the ZG-RQ-01
  activity-groups failure, Fuwen run `40187614750`, and rehearsal attempt 1
  above. The recorded 64-execution local batch passed 64/64; hosted attempt 2
  passed without any change, so the failure is intermittent/environment-
  dependent and the cause remains unestablished. No runtime change, retry or
  relaxed barrier was added. The gate correctly refuses when it reproduces.
- **ZG-RQ-01-OB-01** (`SQLite Error 13` disk-full observation) retained
  separately; no new occurrence.
- **Single-owner limitation:** `prevent_self_review` cannot be `true` without a
  second reviewer; `can_admins_bypass: false` is the compensating control.
  Owner approval is a human step; no automation approves publication.
- **Governance residuals:** owner can change workflows/environments/secrets;
  no deployment branch policy on the environment; `main` unprotected; actions
  referenced by mutable major tags (`sha_pinning_required: false`).
- **No actual publication exercised:** the tag path, OIDC exchange and NuGet
  trusted-publisher policy were not executed; a policy mismatch would fail
  closed at `NuGet/login`.
- **Equivalence limits:** archives are semantically equivalent, not
  byte-identical; Fuwen's staged feed is a per-run source rebuild, not the
  frozen archive bytes.
- **Independent review pending:** the author cannot self-certify.

## G. Verdict and independent review

- Provisional author verdict: **QUALIFIED WITH QUALIFICATIONS** — RG-01,
  RG-02 and RG-03 are remediated with positive and negative evidence and
  fail-closed behavior; the qualifications are REL-SQLITE-01 (OPEN, fail-closed
  when it reproduces), the single-owner approval limitation, and the
  not-byte-reproducible/not-actually-published limits above.
- **The author does not certify this packet.** An independent reviewer (not the
  author) must adversarially review this packet and the implementation before
  any qualification claim is final. The review must at minimum: reproduce the
  exact commits/trees; attempt to make a failed, stale, wrong-repo or
  wrong-workflow qualification publish; attempt to substitute or tamper with a
  package and bypass hash/set/dependency checks; attempt to reach the publisher
  without environment approval or without verification; confirm the rehearsals
  structurally cannot push or obtain OIDC; and confirm no tag/publication/merge
  occurred. The reviewer must sign off with one of QUALIFIED / QUALIFIED WITH
  QUALIFICATIONS / REMEDIATION REQUIRED / BLOCKED, and this file must be updated
  with that result (or a separate review file referenced here).
- Until then, this packet is **evidence of a completed remediation attempt, not
  a final qualification**.

## H. How to verify (reproduce)

Commands (any shell; PowerShell 7 for the scripts):

```text
git ls-remote --heads https://github.com/jenolaszlo-sketch/penghou-zhinu.git
git ls-remote --heads https://github.com/jenolaszlo-sketch/penghou-fuwen.git
gh run view 40311550947 --repo jenolaszlo-sketch/penghou-zhinu   (CI at 009c7f5)
gh run view 40314794489 --repo jenolaszlo-sketch/penghou-zhinu   (final rehearsal)
gh run view 40308636700 --repo jenolaszlo-sketch/penghou-fuwen   (attempt 1 fail preserved / attempt 2 success)
gh api repos/jenolaszlo-sketch/penghou-zhinu/environments/nuget
gh api repos/jenolaszlo-sketch/penghou-fuwen/environments/nuget
git diff f0edf1714013106bdbce090179342ccb029eaf83 009c7f5876a2dd6133f2ebc80e0806790e6e9aba -- src Directory.Build.props NuGet.config
git diff 8c7da4cfbc882804d801fe0e8b073733e4ee563c 65658227151cfbcc69fde752832e4b6b0691d861 -- src Directory.Build.props NuGet.config
pwsh -NoProfile -File eng/Invoke-ReleaseRehearsal.ps1   (offline matrices; see script parameters)
pwsh -NoProfile -File eng/Verify-QualificationRun.ps1 -Repository jenolaszlo-sketch/penghou-zhinu -CommitSha <historical-sha> -GitHubToken (gh auth token)   (R02/R04/R05/R13 refusals)
```

Never run the publisher (`publish.yml`) or push a `v*` tag as part of review;
the rehearsal workflow is the nonpublishing exercise of the same verification
path.
