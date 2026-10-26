# Known findings and open items

This file distinguishes **remediated Q6 findings** from **open items that are
not claimed as fixed**. Nothing here authorizes publication.

## Remediated in this milestone

| ID | Finding | Resolution | Evidence |
| --- | --- | --- | --- |
| RG-01 | Zhinu publication was not bound to a successful qualification of the exact commit and exact artifacts | Two-stage release: CI emits a manifest + artifact set; publisher verifies the exact-commit run through the GitHub API and publishes only those artifacts behind `environment: nuget` | `rg-01-zhinu.md`, `local/artifact-matrix.json`, `local/api-offline-03.json`, `local/live-api-negatives.json`, hosted run `40308720683` |
| RG-03 | Fuwen could not guarantee the native Zhinu feed | Exclusive source mapping + staged source rebuild from the pinned commit, verified before restore; publisher consumes only validated artifacts | `rg-03-fuwen.md`, `../Penghou.Fuwen/docs/evidence/zg-rq-02/local/feed-rehearsal-02.json`, hosted run `40308636700` |
| RG-02 | Publication environment was not demonstrably enforcing approval | `required_reviewers` on both `nuget` environments, admins cannot bypass, live pending/approval cycle exercised | `rg-02-environment.md`, `local/environment-*-nuget-2026-10-26.json`, Zhinu rehearsal `40308720683` |

## Open — not remediated

### REL-SQLITE-01 (OPEN)

`SQLite Error 10: 'disk I/O error'` during process-loss lease recovery on
Windows. Third occurrence observed during Fuwen rehearsal `40308636700`
attempt 1 (2026-10-26, windows-latest, .NET 10, blocked item "b"); attempt 2 of
the same run and commit then passed without any change, so the failure is
intermittent/environment-dependent and the cause remains unestablished. The
recorded 64-execution local stress batch passed 64/64. No SQLite runtime change,
retry, delay or relaxed barrier was added. The release gate correctly refused to
proceed when the hosted test failed and proceeded only after a clean pass.
Details: `../Penghou.Fuwen/docs/evidence/zg-rq-02/stress-qualification.md` and
the preserved logs under `../Penghou.Fuwen/docs/evidence/zg-rq-02/local/`.

**Effect on this verdict:** the Fuwen hosted rehearsal cannot reach its
handoff/environment checkpoint while this reproduces. That is fail-closed, not
a release-path regression. A future release attempt must either pass the
existing test or establish the root cause and correction; a flaky retry is not
closure.

### ZG-RQ-01-OB-01 (retained separately)

`SQLite Error 13: 'database or disk is full'` on a published preview.4 run with
~3.4 GB free. It is a distinct observation and is **not** merged into
REL-SQLITE-01. No new occurrence in this milestone.

### Environment / governance limitations

- Single owner/reviewer: `prevent_self_review` is `false`; enabling it would
  deadlock publication. `can_admins_bypass: false` is the compensating control.
- The owner can edit workflows, environments and secrets; no immutability
  guarantee is claimed.
- The `nuget` environment has no deployment branch policy. Ref/commit checks in
  the workflows are the binding controls.
- `main` is not branch-protected (pre-existing).
- Actions are not pinned to commit SHAs (`sha_pinning_required: false`);
  mutable major tags remain a supply-chain risk.
- The NuGet.org trusted-publishing policy for `NUGET_USER` was not accessed;
  OIDC issuance/push was never attempted.

### Qualification-method limits

- Zhinu's hosted Stage-A artifacts are **semantically equivalent**, not
  byte-identical, to the recorded candidate set: identical assembly types,
  references and method IL; different MVIDs, PDB checksums, ZIP metadata, XML
  line endings and the expected nuspec repository binding. See
  `artifact-equivalence.md`.
- Fuwen's staged native feed is a per-run source rebuild; its archive hashes
  differ from the frozen pin's recorded archive hashes by design. The binding
  contract is identity/version/dependency/source commit plus the rebuild
  manifest.
- No tag was created, no package was published, and no canonical branch was
  merged. The publisher's real tag path is verified by inspection and by
  nonpublishing rehearsal of the same jobs, not by an actual publication.
- No independent party has yet signed this packet; the handoff records the
  independent-review requirement and the author's non-self-certification.
