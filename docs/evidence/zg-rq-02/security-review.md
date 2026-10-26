# Release trust-boundary review

The source baseline and original Q6 evidence are authoritative. Publication is
unauthorized for this assignment. Production source/APIs/package versions are
unchanged. Implementation tests are not independent qualification.

Publish triggers remain explicit tag pushes/manual dispatch, never ordinary
branch pushes or PR events. Fuwen requires the checked-in version tag and manual
tag confirmation. Zhinu retains manual-main dispatch bound to that exact SHA;
tag use must match checked-in version and resolve to the qualified commit.
Inputs are read through environment variables or validated script parameters,
not untrusted shell source. API-derived run IDs are numeric. No PR-produced
qualification is accepted by Zhinu. Both publishers grant OIDC only to the final
`environment: nuget` job after successful verification/validation. No build or
pack occurs in the publisher. Rehearsals request neither OIDC nor NuGet secrets.

Zhinu's authenticated API selects the latest allowed run of the canonical CI
workflow at the exact SHA, requires both platform jobs, checks same repository,
artifact run/source, non-expiration and manifest attempt/run/source. It rejects
an existing download destination, ambiguous artifact names and stale attempts.
The offline verifier rejects wrong profiles, identities, versions, canonical
filenames, duplicate/unsafe ZIP members, source metadata, hashes and missing/
extra package/symbol sets. A manifest alone cannot grant qualification authority.

Fuwen builds the tracked clean native commit; its per-run rebuild manifest binds
the original tracked contract hash and exact archive hashes. Both validation
platforms consume that immutable feed and reverify it before an empty-cache
restore. Exact packageSourceMapping entries restrict the seven native IDs to
the staged source; that source has no wildcard authority. Public dependencies
remain distinct. Final packages have source/run/version/hash manifests, checked
again after download. Rehearsal calls the same reusable qualification workflow.

Both environments were changed on 2026-10-26 to require reviewer approval:
`required_reviewers` = the sole admin owner, `can_admins_bypass: false`,
`prevent_self_review: false` (single-owner account; enabling it would deadlock
publication) and no branch/tag restrictions. Raw API captures are in
`local/environment-*-nuget-2026-10-26.json`; this is live configuration, not
YAML self-assertion. Pending protected jobs were exercised live in both
repositories (Zhinu runs `40303575468`, `40308720683`, `40314794489`; Fuwen
`40308636700` attempt 2): the run paused, the owner approved explicitly, and the
checkpoint job then ran only its echo step with no credentials. With one owner,
the checkpoint demonstrates a manual approval step, not separation of duties or
refusal of an actual administrator bypass attempt. Static workflow mutation
tests reject removed gates/unauthorized triggers; those are inspection/simulation
evidence. No production approval or bypass was attempted, and no publication
credentials were requested or used by any rehearsal.

Residual limits: external NuGet trusted-publisher subject/audience policy was
not accessed or changed; no OIDC issuance or publication was attempted. Mutable
upstream action version tags remain as in the repository's action policy;
resolved action SHAs are in hosted logs, but action-update governance is not
independently qualified here. A malicious owner can change workflows/settings;
the existing sole-owner governance is not an independent authorization boundary.
No environment secrets exist; NUGET_USER is a repository secret name, and its
value was never read. Only generated read-only Actions tokens are used in hosted
qualification. There is no transfer of production publication credentials.
