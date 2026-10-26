# RG-01: Zhinu exact-commit release qualification

Verdict: **RESOLVED** — publication is bound to a successful qualification of
the exact source commit and to the exact qualified artifacts, with fail-closed
negative coverage. Evidence classes are marked: [LOCAL] local automated fixture
or matrix, [HOSTED] GitHub-hosted execution, [INSPECT] source/config
inspection, [REUSE] historical ZG-RQ-01 evidence.

## Architecture

Two-stage release (`.github/workflows/ci.yml` Stage A,
`.github/workflows/publish.yml` Stage B):

**Stage A — qualification (ci.yml).** After build, format, full tests, pack,
package-closure and legacy-binary probes all succeed on the exact commit, the
ubuntu producer runs `eng/New-ReleaseQualificationManifest.ps1`, which freezes
`release-qualification-manifest.json` (schema version 1: repository, commit SHA,
qualification workflow path, run id/URL, conclusion, timestamp, package version,
the seven package/symbol archives with SHA-256, target frameworks, dependency
pins) and uploads it together with the packages as
`release-qualification-ubuntu-latest`. A manifest can never assert its own
authority: Stage B re-derives authority from GitHub's run records.

**Stage B — publication (publish.yml).**
1. Resolve the exact publication commit (tag ref or dispatch ref;
   `eng/Verify-ReleaseRef.ps1` enforces tag/version equality for tags).
2. `eng/Verify-QualificationRun.ps1` queries the authenticated GitHub API for
   runs at that exact SHA: canonical workflow file/path only, allowed events
   (`push`, `workflow_dispatch`; pull-request runs never accepted), completed +
   successful only, same repository, selectable latest run, artifact exists and
   is not expired; downloads that run's artifact set.
3. `eng/Verify-ReleaseArtifacts.ps1` validates the manifest schema, commit,
   workflow path, run id, version, exact seven-package set, every SHA-256,
   absence of unlisted files, target frameworks, and the accepted
   `[0.1.0-preview.2]` neutral dependency pin against the packed core nuspec.
4. Only then may the publish job run, behind `environment: nuget`
   (approval, see [rg-02-environment.md](rg-02-environment.md)). The publish
   job downloads the verified set, checks it out again with a fresh verifier
   checkout, re-runs `Verify-ReleaseArtifacts.ps1` **before** requesting any
   credential, then obtains an OIDC temporary key and pushes exactly those
   files. The workflow never packs or builds.

## Negative coverage (fail-closed before any publication side effect)

| Scenarios | Class | Evidence |
| --- | --- | --- |
| R02 absent, R03 pending, R04 failed, R05 cancelled/skipped, R13 untrusted/other workflow, R06 wrong commit, R07 hash, R08 missing package, R09 extra package, R10 identity/version, R11 dependency pin, R12 malformed manifest, R14 other run, plus pull-request, wrong-repository, stale-attempt and missing-matrix variants | [LOCAL] | `local/api-offline-03.json` — 19 passing fixtures against the production downloader via an offline API surface; `local/artifact-matrix.json` — R01/R06-R12/R14/R18 |
| Real historical runs: R02 (no runs; old-format success without a Stage-A artifact), R04 (failed run), R05 (cancelled run), R13 (no canonical-workflow run) | [LIVE-API, read-only] | `local/live-api-negatives.json` — the production verifier was executed locally against the authenticated GitHub API for each historical case; every case exited 1 with the intended refusal. The hosted rehearsal's API step is a separate **offline** fixture suite (`local/api-offline-03.json`); it does not claim live-API coverage |
| Workflow mutation / static policy regressions (removed gates, unauthorized triggers, ambient OIDC) | [LOCAL] | `eng/test_workflow_boundaries.py`, executed **locally** in both repositories; raw outputs `local/zhinu-workflow-boundaries.json` and `local/fuwen-workflow-boundaries.json`, classified "STATIC WORKFLOW MUTATION; NOT HOSTED ENFORCEMENT". Hosted no-push/no-OIDC properties are evidenced by workflow inspection and the hosted run records, not by this test |
| Credential ordering: verification precedes `NuGet/login`; publish job re-verifies after download | [INSPECT + LOCAL] | `release-rehearsal.yml` handoff job reproduces the publisher's final verification without credentials; structural guard step |

Invalid first attempts are preserved, not erased: the initial offline
API-mock harness attempts (`local/api-offline-01.json`, `-02`) failed for
harness reasons and are retained as invalid observations (`observations.md`).

## Hosted rehearsal (nonpublishing)

Final run `40314794489` on `codex/zg-rq-02-rehearsal` at implementation commit
`009c7f5` (the current branch head), plus the earlier runs below:

- "Verify qualified release path (no push)": SUCCESS — resolved the real CI
  qualification run of `009c7f5` (run `40311550947`), downloaded its artifact
  set, ran the offline matrix (R01/R06-R12/R14/R18), the offline API/tag
  boundary fixtures, and uploaded the verified handoff set. (The static
  workflow-boundary test is executed locally, not in this hosted job; see the
  negative-coverage table.)
- "Reverify publication handoff without credentials": SUCCESS — re-executed the
  publisher's exact final artifact verification against the uploaded/downloaded
  handoff set (the same script/arguments the publisher uses), with no
  credentials.
- "Nonpublishing environment checkpoint": reached only after both jobs
  succeeded and **waited for explicit approval** (pending deployment; approved
  by the owner with a nonpublishing comment; checkpoint echo only, no
  credentials, no push). The run concluded SUCCESS without any publication.
  Raw records: `local/hosted-rehearsal-40314794489*`.
- Earlier rehearsal `40308720683` at `908555d` passed the identical path
  (qualification run `40303504133`), and `40303575468` at `ced5133` passed
  verification plus the environment checkpoint the same way.

## Limits

- The publisher's behavior at a real tag push was verified by inspection and
  by the nonpublishing rehearsal of identical jobs; no tag was created and no
  push occurred (prohibited by the milestone).
- GitHub's trusted-publisher (OIDC subject/audience) policy at NuGet was not
  accessed or altered; OIDC issuance/push was never attempted.
- Mutable action major-version tags remain the repository's existing action
  policy; resolved SHAs appear in hosted logs but action-update governance is
  not qualified here.
- The repository has a single owner; workflow/settings mutation by that owner
  is outside any technical boundary (recorded in
  [rg-02-environment.md](rg-02-environment.md)).
