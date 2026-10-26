# Permissions and trust-boundary review

Scope: publication credentials, workflow token permissions, repository and
environment access, action supply chain, and ref/event controls. Raw API
captures are in `local/` (`actions-permissions-*.json`,
`actions-workflow-permissions-*.json`, `repo-secret-names-*.json`,
`environment-secrets-*.json`, `collaborators-*.json`,
`branch-protection-main-*.json`, `environment-*-nuget-2026-10-26.json`).

## Access

- Collaborators: only `jenolaszlo-sketch` (admin) in both repositories.
- Repository secrets: only `NUGET_USER` in both repositories. No long-lived
  NuGet API key is stored.
- `nuget` environment secrets: none (`total_count: 0`). The environment
  protects the job that may request an OIDC token, not a stored push
  credential.
- Default workflow token permission: `read`; GitHub Actions may not approve
  pull-request reviews.

## Token scoping in the release workflows

| Workflow/job | Token permissions | OIDC |
| --- | --- | --- |
| Zhinu `ci.yml` | `contents: read` | no |
| Zhinu `publish.yml` `verify` | `contents: read`, `actions: read` | no |
| Zhinu `publish.yml` `publish` | `contents: read`, `id-token: write` | yes, only here |
| Zhinu `release-rehearsal.yml` `rehearse` | `contents: read`, `actions: read` | no |
| Zhinu `release-rehearsal.yml` `handoff` / `authorization-probe` | `contents: read` | no |
| Fuwen `release-qualification.yml` | `contents: read` | no |
| Fuwen `publish.yml` `publish` | `contents: read`, `id-token: write` | yes, only here |
| Fuwen `release-rehearsal.yml` | `contents: read` | no |

No rehearsal job can obtain an OIDC token or a NuGet key. The publisher's
`id-token: write` is granted only to the final job, after verification and
behind the `nuget` environment approval.

## Ref and event controls

- Zhinu `eng/Verify-ReleaseRef.ps1` accepts exactly two publication paths:
  a manual `workflow_dispatch` on branch `main`, or a tag ref named
  `v<checked-in version>` that resolves to the publication commit. Every other
  ref is refused. The manual path is still bound to the exact SHA through the
  API-verified qualification run.
- Zhinu `eng/Verify-QualificationRun.ps1` accepts only `push` and
  `workflow_dispatch` qualification runs; pull-request runs are never trusted.
  The run must be the canonical `ci.yml` at its canonical path, completed,
  successful, in the same repository, with both platform jobs successful and a
  non-expired `release-qualification-ubuntu-latest` artifact whose manifest
  matches the run, attempt and source.
- Fuwen `release-qualification.yml` refuses publication unless the ref is a tag
  equal to the checked-in version tag and (for manual dispatch) the supplied
  `release_tag` equals the selected ref. The rehearsal calls the same workflow
  with `require-release-tag: false`; the publisher calls it with `true`.
- `publish.yml` in both repositories triggers only on `v*` tag pushes and
  explicit `workflow_dispatch`, never on ordinary branch pushes or pull
  requests.

## Supply chain and residual risks

- `sha_pinning_required` is `false` and `allowed_actions` is `all`. Actions are
  referenced by mutable major tags (`actions/checkout@v6`,
  `actions/setup-dotnet@v5`, `actions/upload-artifact@v4/v6`,
  `actions/download-artifact@v4/v7`, `NuGet/login@v1`). A compromised or
  retargeted major tag could execute in the release path. This is the
  repository's pre-existing action policy; resolving actions to commit SHAs is
  recommended but is outside this Q6 remediation.
- The `main` branch is not protected (GitHub API returns "Branch not
  protected"). No merge to `main` was performed in this milestone. Branch
  protection is a repository-governance decision outside Q6.
- The NuGet.org trusted-publishing policy for `NUGET_USER` was not accessed or
  altered. OIDC issuance and push were never attempted, so the external policy
  is unverified; a mismatch would fail closed at `NuGet/login`.
- The repository owner can edit workflows, environment settings and secrets.
  No technical control prevents an owner from doing so; the evidence records
  configuration at review time, not immutability.
- Single-owner account: `prevent_self_review` cannot be enabled without a
  second reviewer (see `rg-02-environment.md`).

## Verdict

No new long-lived publication credential was introduced. The only push
credential is the short-lived OIDC key requested in the protected final job.
Token permissions are least-privilege for the release path, and rehearsals
cannot obtain publication credentials. The residual supply-chain, branch-
protection and single-owner risks are recorded, not waived.
