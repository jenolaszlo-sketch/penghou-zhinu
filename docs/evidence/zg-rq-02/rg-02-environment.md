# RG-02: publication environment enforcement

Verdict: **ENFORCED, with a recorded single-owner limitation.** The gate is a
real GitHub environment protection rule, verified through the GitHub API and
exercised live by the hosted rehearsal; YAML alone is not claimed to enforce
approval.

## Applied configuration

Applied to the `nuget` environment of both repositories
(`jenolaszlo-sketch/penghou-zhinu`, `jenolaszlo-sketch/penghou-fuwen`) on
2026-10-26 via the GitHub API. Raw responses:
`local/environment-zhinu-nuget-2026-10-26.json`,
`local/environment-fuwen-nuget-2026-10-26.json`; recovery copies under
`recovery/environments-nuget.json` and
`recovery/environments-nuget-deployment_protection_rules.json`.

| Setting | Zhinu | Fuwen |
| --- | --- | --- |
| Protection rule | `required_reviewers` | `required_reviewers` |
| Reviewer | `jenolaszlo-sketch` (user id `312601944`) | `jenolaszlo-sketch` (user id `312601944`) |
| `can_admins_bypass` | `false` | `false` |
| `prevent_self_review` | `false` (owner decision) | `false` (owner decision) |
| Deployment branch policy | none (`null`) | none (`null`) |

`prevent_self_review: true` was deliberately not set: this repository has a
single owner/reviewer, and GitHub would then require a different reviewer,
deadlocking every publication. This is an owner-decision limitation, not a
claim that self-review is prevented. `can_admins_bypass: false` is the
compensating control that prevents the owner from bypassing the approval
queue.

## Live enforcement evidence (GitHub-hosted)

Zhinu rehearsal run `40308720683` at `908555d`:

1. `verify` and `handoff` jobs completed successfully with no publication
   credentials.
2. The `authorization-probe` job (`environment: nuget`) did not start. The run
   reported `status: waiting`; the pending-deployment API returned the `nuget`
   environment (environment id `25323474414`), reviewer
   `jenolaszlo-sketch`, and `current_user_can_approve: true`.
3. The owner explicitly approved the nonpublishing checkpoint with the comment
   "Owner approval for the ZG-RQ-02 nonpublishing rehearsal checkpoint at
   908555d. The checkpoint job requests no credentials and performs no
   publication." The job then ran only
   `echo 'Manual checkpoint reached. No credentials requested; publication remains unauthorized.'`
   and the run concluded `success`.
4. No OIDC token, NuGet key or push occurred in any rehearsal job.

Final Zhinu rehearsal `40314794489` at `009c7f5` repeated the cycle: pending
deployment on environment `nuget` (id `25323474414`, reviewer
`jenolaszlo-sketch`, `current_user_can_approve: true`), explicit owner approval
with a nonpublishing comment, checkpoint echo only, run SUCCESS. Raw records:
`local/hosted-rehearsal-40314794489-pending-deployments.json`,
`local/hosted-rehearsal-40314794489-approval.json`. The earlier Zhinu rehearsal
`40303575468` at `ced5133` went through the same pending-deployment/approval
cycle and also completed without publication.

Fuwen rehearsal run `40308636700` exercised both directions:

- attempt 1: the dependent `authorization-probe` job was **skipped** after the
  windows qualification failed (REL-SQLITE-01 recurrence, see
  `stress-qualification.md`). A failed qualification prevents the environment
  checkpoint and the publisher from running — the fail-closed direction.
- attempt 2: after the same commit passed, the run paused at the pending
  deployment on the Fuwen `nuget` environment (id `22645578735`, reviewer
  `jenolaszlo-sketch`, `current_user_can_approve: true`), the owner approved
  explicitly, and the checkpoint job then ran only its echo step with no
  credentials; run SUCCESS. Raw records:
  `../Penghou.Fuwen/docs/evidence/zg-rq-02/local/hosted-rehearsal-40308636700-attempt2-pending-deployments.json`
  and `...-approval.json`.

## What the publisher actually protects

- Zhinu `publish.yml`: `publish` has `needs: verify`, `environment: nuget`,
  and the only `id-token: write` permission. The verify job has `actions: read`
  and no OIDC. Push steps exist only inside `publish`, after a fresh verifier
  checkout and `Verify-ReleaseArtifacts.ps1` run against the downloaded set,
  before `NuGet/login`.
- Fuwen `publish.yml`: `publish` has `needs: validate`, `environment: nuget`,
  and the only `id-token: write` permission. It downloads only
  `release-packages-${{ github.sha }}-ubuntu-latest`, re-runs
  `ReleasePackageSet.ps1 -Mode Verify` from a fresh checkout, then requests the
  OIDC key. The reusable `release-qualification.yml` has `contents: read` only.

## Limits and owner actions

- The repository owner can edit workflows, environment settings and repository
  secrets. No technical control can prevent an owner from doing so; the
  evidence records the configuration at the time of review, not an immutability
  guarantee.
- Approval remains a human step. The environment cannot approve itself, and no
  automation was added to approve publication.
- No environment secret values were read or changed. The only publication
  secret referenced is `NUGET_USER`; the push credential is the short-lived
  OIDC key returned by `NuGet/login`.
- The deployment branch policy is unset. The publisher's own ref/commit checks
  (`Verify-ReleaseRef.ps1`, exact-tag equality, exact-commit qualification)
  remain the binding controls; a branch-policy restriction can be added by the
  owner later without changing this evidence.
- `prevent_self_review: false` is accepted only because there is one owner. If a
  second trusted reviewer is added, setting it to `true` is recommended.
