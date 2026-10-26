# Independent adversarial review record

Recorded verdict: **QUALIFIED WITH QUALIFICATIONS**. This review was performed
by reviewer sessions separate from the author, in the same verification
environment, read-only (git/`gh`/file reads plus re-execution of the production
verifier scripts against the live GitHub API). The author did not and cannot
self-certify; this file records the reviewer's findings and verdict.

## Materials reviewed

- Implementation commits: Zhinu `009c7f5876a2dd6133f2ebc80e0806790e6e9aba`,
  Fuwen `65658227151cfbcc69fde752832e4b6b0691d861`.
- Evidence packets at the time of round 1: Zhinu `18af51b`/`66256fb`, Fuwen
  `8a18a3f`/`94358ed` (`docs/evidence/zg-rq-02/` in both repositories,
  including `local/` raw captures).
- Workflows (`ci.yml`, `publish.yml`, `release-rehearsal.yml`,
  `release-qualification.yml`), release scripts under `eng/`, hosted run
  records and live GitHub environment configuration.

## Round 1 — REMEDIATION REQUIRED (one finding)

The reviewer confirmed every structural check (exact commits/trees/parents;
empty production deltas; all five live-API negatives refusing with exit 1 and
the intended messages; publisher ordering download → re-verify → `NuGet/login`;
live environment protection rules; rehearsals lacking push/OIDC capabilities;
REL-SQLITE-01 honestly OPEN and fail-closed; artifact-equivalence claims
corroborated by independent recomputation; no tags, no publication, `main`
unmoved; no secrets in the packet) but returned **REMEDIATION REQUIRED** for one
false provenance claim:

> `rg-01-zhinu.md` and `rehearsal-matrix.md` (in both packets) stated that
> `eng/test_workflow_boundaries.py` was "executed in both hosted rehearsals" /
> "ran in the hosted rehearsals". The test is in fact executed locally only;
> its own raw outputs are classified "STATIC WORKFLOW MUTATION; NOT HOSTED
> ENFORCEMENT". The reviewer verified no workflow or hosted log invokes it.

The reviewer explicitly found **no counterexample** that would let a failed,
stale, wrong-repo or wrong-workflow qualification, a substituted or unverified
artifact, or an unapproved job publish; the finding was a documentation
overclaim only.

## Remediation

- Zhinu commit `0be6cd9f685f5d5c4e68cf9ee1a2ce843bd3daf7` and Fuwen commit
  `f2d044f70570a710ca4bfe0bba186aa2a4499d7a` (both docs-only).
- Corrected the workflow-mutation row, the hosted-rehearsal bullet and the
  rehearsal-matrix bullet to state **local execution** with the
  "STATIC WORKFLOW MUTATION; NOT HOSTED ENFORCEMENT" classification, and to
  attribute the hosted no-push/no-OIDC properties to workflow inspection plus
  the hosted run records.
- Recorded the correction in `observations.md` (both packets); fixed a
  git-binding parenthetical about which Fuwen test files changed; normalized
  UTF-16LE API captures to UTF-8 (content verified character-identical).

## Round 2 — QUALIFIED WITH QUALIFICATIONS

A second reviewer session re-verified the remediation and re-issued the
verdict:

- The corrected wording is present and consistent in all four documents
  (canonical and mirrors); a full grep sweep found **no residual hosted-execution
  claim**.
- The remediation commits are docs-only; the encoding normalization was
  verified content-preserving via exact same-text comparison.
- The reviewer independently re-ran two of the five live-API negatives
  (`d4968ea` → R02 refusal; `publish.yml` at `18c0624` → R13 refusal), both
  exit 1 with the intended messages.
- Re-confirmed: rehearsal workflows contain no `id-token`,
  `NuGet/login` or push capability; publisher ordering; live environment
  configuration (`required_reviewers`, `can_admins_bypass: false`); no tags
  (`v0.2.0-preview.10` absent), no publication (nuget.org 404), `main` unmoved
  (`f0edf17` / `8c7da4c`); normalized JSON integrity (deployment ids, job
  conclusions).
- Verdict text: **"QUALIFIED WITH QUALIFICATIONS"**, the qualifications being
  the carried-over pre-existing limits below — not remediation defects.

## Carried qualifications (unchanged)

1. **REL-SQLITE-01 OPEN** — hosted attempt-1 failure preserved; attempt 2
   passed without any change; 64/64 local batch passed; cause unestablished;
   the gate fails closed when it reproduces and a green rerun is not closure.
2. **Single-owner approval limitation** — `prevent_self_review: false`
   (enabling it would deadlock publication without a second reviewer);
   `can_admins_bypass: false` is the compensating control; the checkpoint
   demonstrates a manual approval step, not separation of duties.
3. **Artifacts semantically equivalent, not byte-identical** — assemblies have
   identical types, references and method IL; archive bytes, MVIDs, PDB
   checksums, ZIP metadata and XML line endings differ by build environment.
4. **No actual publication exercised** — the tag path, OIDC exchange and NuGet
   trusted-publisher policy were not executed; a policy mismatch would fail
   closed at `NuGet/login`.

## Reviewer's minor follow-ups and disposition

1. Dangling sentence fragment in the Fuwen `observations.md` — **fixed**
   (remediation commit for round 2).
2. `independent-review.md` referenced but not yet present — **fixed by this
   file**.
3. Pre-existing em-dash mojibake in the Fuwen mirror documents (introduced by
   earlier PowerShell 5.1 ANSI misreads during mirroring) — **fixed
   cosmetically**; no content change.

## Limits of this review

- The reviewers ran read-only; they did not attempt to modify workflows,
  environments or packages, and did not execute the publisher or push a tag.
- The review is an adversarial inspection plus targeted re-execution; it is
  not a formal certification body, a penetration test of GitHub itself, or a
  guarantee against a malicious repository owner (no technical control can
  bind an owner who can rewrite workflows and settings).
- Any future change to the workflows, environment configuration or artifacts
  invalidates this verdict and requires a new review.
