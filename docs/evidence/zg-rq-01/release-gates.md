# Q6: release automation and publication safety

Findings below are inspection conclusions over the checked-in workflow files
at the bound commits (Zhinu `f0edf17`, Fuwen `8c7da4c`). No publication,
tag or workflow dispatch was attempted or performed by this qualification.

## Zhinu publish workflow (`.github/workflows/publish.yml`)

Triggers: `push: tags: v*` and `workflow_dispatch` only. There is **no
publish-on-merge or branch-push trigger**: ordinary commits, documentation
pushes and merges cannot publish packages (Q6.4 pass). OIDC-issued temporary
API keys; `--skip-duplicate` prevents updating an existing version.

**Finding RG-01 (enforcement gap — remediation required).** The workflow packs
and pushes whatever its checkout ref resolves to and performs no automated
verification that CI qualified that exact commit. On the dispatch path it
packs the current `main` HEAD with no version input; on the tag path it packs
the tag ref. `docs/releasing.md` documents the intended control as an
operator procedure ("it does not wait for or rerun ordinary CI, so check that
CI has passed before dispatching it"), which is a documented but **unenforced**
gate: nothing in automation prevents dispatching a publish for a commit whose
CI failed or never ran. Per this milestone's Q6.1 acceptance ("publication
requires a qualified immutable source binding"), the chosen mechanism must be
made enforceable. Narrow remediation options (owner to select):
add a pre-publish job that verifies the CI conclusion for the exact dispatch
SHA is `success` (and fails the publish otherwise), and/or require a protected
environment with approval, and/or publish only verified immutable CI artifacts
produced by the qualifying run.

**Q6.2 (Zhinu).** Tag pushes build from the tagged commit by construction
(actions/checkout of the tag ref) and the tag overrides the package version;
this is sound as long as tags are owner-created at approved commits. The
dispatch path builds from `main` HEAD — pinning is the operator's procedural
check today, which is precisely the RG-01 gap.

## Fuwen publish workflow (`.github/workflows/publish.yml`)

Triggers: `push: tags: v*` and `workflow_dispatch` with a required
`release_tag` input. Strong built-in binding (positive findings):

- The run must originate from an existing version tag; the tag must equal the
  checked-in version; a manual dispatch must re-enter the exact tag name
  (mismatch rejected) — Q6.2 pass and a partial negative test by construction.
- The `validate` job rebuilds, formats, tests, verifies the canonical JSON
  vector, packs exactly the four packages and uploads them keyed by
  `github.sha`; the `publish` job consumes **only** those uploaded artifacts
  and re-checks the complete package set — publication is artifact-bound to the
  validation of the exact tagged commit (this is the "verified immutable CI
  artifact" mechanism for the Fuwen side).

**Finding RG-02 (environment protection unconfigured — owner decision).** The
`publish` job declares `environment: nuget`, but the repository environment
currently has **no protection rules and no required reviewers** (verified via
the repository environments API at inspection time). The environment therefore
adds no approval gate today. The owner should either configure required
reviewers on the `nuget` environment (making authorization explicit and
auditable) or record that tag creation is the single authorization point.

**Finding RG-03 (Fuwen native-feed gate — required, missing prerequisite;
remediation required before any Fuwen tag).** Root `NuGet.config` maps all
seven native package IDs **exclusively** to the local folder
`artifacts/zhinu-candidate` (most-specific-pattern mapping; no fallback to the
public `*` mapping for those IDs), and the publish workflow contains no step
that prepares that directory. Since the native preview.10 packages are also
absent from the public feed, any Fuwen tag run today fails closed during
restore (the gate blocks rather than silently substituting versions — which is
the correct fail-closed direction). Determination per Q6.3: the gate is
**required and missing a valid prerequisite, and the workflow/feed wiring is
incomplete** (not obsolete, not an intentional block of an unsupported path).
This matches the already-recorded packet gate ("Fix and validate the release
feed path before creating a tag"). Remediation options (owner to select):
prepare the candidate feed inside the workflow (the same pinned-source
preparation the CI already performs), or — after native publication and
indexing — repoint the exact native-ID mappings at nuget.org with fresh public
archives. It must not be bypassed.

## Q6.5 negative-path conclusions (inspection-based)

Verified by workflow/config inspection; no live publication attempts were
made: no branch-push or merge can trigger either publisher; Fuwen rejects
tag/version mismatches before packing; both publishers push a counted,
verified package set (a missing package fails the step; Zhinu's glob would
fail `nuget push` on no files); an unprepared native feed fails Fuwen restore
closed (RG-03 blocks publication exactly as intended); `--skip-duplicate`
makes version reuse a no-op rather than an overwrite. Publication
authorization rests on tag-creation/dispatch permissions in both repositories;
with RG-02 as recorded, the Fuwen path has no second approval control.

## Verdict

Q6 = **REMEDIATION REQUIRED**: findings RG-01 (Zhinu exact-commit CI binding
unenforced) and RG-03 (Fuwen native-feed prerequisite missing) must be
repaired and verified before publication can proceed under this milestone's
acceptance criteria; RG-02 is an owner decision to record or resolve. The
Fuwen artifact-bound publish design is otherwise the enforceable pattern the
specification asks for, and the Zhinu workflow needs its missing exact-commit
verification added.
