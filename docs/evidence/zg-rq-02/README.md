# ZG-RQ-02: release automation remediation and Q6 delta qualification

Status: **implementation complete; independent adversarial review recorded in
[independent-review.md](independent-review.md) — verdict QUALIFIED WITH
QUALIFICATIONS; publication remains unauthorized.**

Predecessor: ZG-RQ-01 (`codex/zg-rq-01`, `d272b26`), verdict REMEDIATION
REQUIRED for gate Q6 only. This milestone remediates findings RG-01 (Zhinu
exact-commit publication binding), RG-03 (Fuwen native-feed preparation) and
RG-02 (publication environment approval), preserves the ZG-RQ-01 technical
qualification (Q1-Q5), and executes the recorded REL-SQLITE-01 recurrence
response as separately tracked work.

## Baselines and implementation

| Repository | Accepted baseline | Implementation branch | Implementation commit |
| --- | --- | --- | --- |
| Penghou.Zhinu | `f0edf1714013106bdbce090179342ccb029eaf83` | `codex/zg-rq-02-zhinu` | `009c7f5876a2dd6133f2ebc80e0806790e6e9aba` |
| Penghou.Fuwen | `8c7da4cfbc882804d801fe0e8b073733e4ee563c` | `codex/zg-rq-02-fuwen` | `65658227151cfbcc69fde752832e4b6b0691d861` |

Exact commits, trees, parents, worktree state and push status:
[git-binding.md](git-binding.md). Production delta vs baseline (`src/`,
`Directory.Build.props`, `NuGet.config`) is **empty in both repositories**
(`scope-check.json`); only workflows, release-verification scripts, tests,
release documentation and evidence changed. Candidate identities are unchanged:
Zhinu `0.2.0-preview.10` / schema 10, Fuwen `0.1.0-preview.20`, neutral pin
`[0.1.0-preview.2]`.

## What changed

- **RG-01 (Zhinu):** two-stage release. `ci.yml` now freezes a
  release-qualification manifest (commit, run, conclusion, package identities,
  SHA-256 of every nupkg/snupkg, target frameworks, dependency pins) and uploads
  the qualified package set on the ubuntu producer. `publish.yml` resolves the
  exact publication commit, locates the successful qualification run of that
  commit **through the authenticated GitHub API** (canonical workflow, allowed
  events, both platform jobs, same repository, non-expired artifacts), downloads
  the run's artifact set, verifies manifest/hashes/package set/dependency pins,
  and publishes **only those verified files** — never a rebuild — behind the
  protected `nuget` environment. The publish job re-verifies the downloaded set
  from a fresh verifier checkout before requesting credentials. See
  [rg-01-zhinu.md](rg-01-zhinu.md).
- **RG-03 (Fuwen):** a reusable `release-qualification.yml` stages the exact
  pinned native candidate feed (source commit from
  `docs/qualification/activity-groups/zhinu-candidate.json`, clean-checkout
  enforcement, per-run rebuild manifest, rejection-boundary fixtures) before
  any restore; the exclusive ID mapping in `NuGet.config` can only resolve the
  seven native packages from that staged feed. The publisher consumes the
  validated artifacts and re-verifies every archive before credentials. See
  [rg-03-fuwen.md](rg-03-fuwen.md).
- **RG-02:** both repositories' `nuget` environments now require reviewer
  approval (`required_reviewers` = repository owner), admins cannot bypass, and
  the live approval gate was exercised by both rehearsals (pending deployment →
  explicit approval → checkpoint runs; no publication). Single-owner
  self-review remains a recorded owner-decision limitation. See
  [rg-02-environment.md](rg-02-environment.md).

## Execution results (summary)

| Gate / scenario | Status | Evidence |
| --- | --- | --- |
| RG-01 exact-commit enforcement (positive + R02-R16 offline/live negatives) | PASS | [rg-01-zhinu.md](rg-01-zhinu.md), `local/artifact-matrix.json`, `local/api-offline-03.json` |
| RG-03 staged feed + restore + negatives | PASS | [rg-03-fuwen.md](rg-03-fuwen.md), `../Penghou.Fuwen/docs/evidence/zg-rq-02/local/feed-rehearsal-02.json` |
| RG-02 approval boundary | ENFORCED (live checkpoint) | [rg-02-environment.md](rg-02-environment.md) |
| Hosted Zhinu rehearsal (verification → handoff → environment checkpoint) | PASS — final run `40314794489` at `009c7f5` (earlier `40308720683` at `908555d` retained) | [rehearsal-matrix.md](rehearsal-matrix.md) |
| Hosted Fuwen rehearsal (feed → both platforms validate → handoff → environment checkpoint) | attempt 1 FAILURE preserved (REL-SQLITE-01, fail-closed); attempt 2 PASS after approval | [rehearsal-matrix.md](rehearsal-matrix.md) |
| REL-SQLITE-01 stress response (64 executions) | 64/64 PASS; finding remains OPEN | `../Penghou.Fuwen/docs/evidence/zg-rq-02/stress-qualification.md` |
| Zhinu source/artifact equivalence (baseline vs hosted Stage-A) | Semantically equivalent: identical types/references/method IL in 14/14 assemblies; nuspec differs only in repository binding; not byte-identical | [artifact-equivalence.md](artifact-equivalence.md), `local/artifact-equivalence.json` |
| Fuwen native rebuild equivalence | Payload-identical, ZIP metadata differs | `../Penghou.Fuwen/docs/evidence/zg-rq-02/artifact-equivalence.md`, `local/native-equivalence.json` |

## Preserved failures and limitations

All failed attempts and harness defects are retained with their own run IDs or
fixture directories; nothing was overwritten: the invalid first feed-rehearsal
fixture (strict-mode harness defect), the two offline API-mock attempts, hosted
Fuwen runs `40302339008` (fixture path defect), `40302665778` (hosted formatting
gate) and `40303275879` (workflow-contract test vs the new architecture, fixed
in `6565822`). See [known-findings.md](known-findings.md). No tags were created,
no package was published, no canonical branch was merged, and production
source, package versions and public APIs are unchanged.
