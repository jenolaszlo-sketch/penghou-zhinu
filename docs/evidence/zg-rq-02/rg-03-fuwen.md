# RG-03: Fuwen native-feed resolution

Verdict: **RESOLVED** — the Fuwen release path can only resolve the seven
pinned Zhinu native packages from a staged feed built from the exact pinned
source commit; there is no public-feed fallback and no widened version range.
The hosted Windows validation is separately blocked by the open REL-SQLITE-01
recurrence, which is fail-closed, not a feed-resolution failure.

## Defect and contract

The ZG-RQ-01 finding was that the Fuwen release path could not guarantee that
the native Zhinu packages came from the qualified candidate; a restore could
fail or resolve an unintended package source. The required contract is:

- native source commit `bf33c05cf2390af6924bbe3761e09f9e377c7a97` (pinned in
  `docs/qualification/activity-groups/zhinu-candidate.json`);
- native version `0.2.0-preview.10`;
- exactly seven package IDs: `Penghou.Zhinu`, `.Agents`, `.Hosting`,
  `.Hosting.AspNetCore`, `.OpenTelemetry`, `.Sqlite`, `.Testing`;
- dependency pin `Penghou.Workflow.Abstractions [0.1.0-preview.2]`;
- no resolution from `nuget.org` for those IDs, and no version-range widening.

## Implementation

**Exclusive source mapping.** `NuGet.config` adds the local source
`zhinu-candidate` (`artifacts/zhinu-candidate`) and maps the seven native IDs
to it. The negative restore proof (`R16`, below) shows `nuget.org` is not
considered for those IDs: `NU1101 ... source(s): zhinu-candidate ...
PackageSourceMapping is enabled, the following source(s) were not considered:
nuget.org`.

**Staged feed before validation.** `.github/workflows/release-qualification.yml`
(used by both the publisher and the rehearsal) has a `prepare-native-feed` job
that runs before `validate`:

1. Reads the pin and checks the exact commit/version.
2. Checks out the pinned Zhinu source commit into `native-candidate-source`.
3. Runs `eng/Prepare-ZhinuSourceCandidate.ps1` to build the seven native
   packages from source and emit a per-run rebuild manifest.
4. Runs `eng/Test-ZhinuCandidatePreparation.ps1` rejection boundaries.
5. Runs `eng/Verify-NativeFeed.ps1` with the resolved `git rev-parse HEAD`,
   the pin, the rebuild manifest and the pinned version. It fails closed on a
   missing feed, wrong count, wrong ID/version, source-commit mismatch,
   rebuild-hash mismatch, invalid pin, or dependency-pin drift.
6. Uploads `native-candidate-feed` (the seven nupkg plus the rebuild manifest).

The `validate` job downloads that artifact, restores the
`artifacts/zhinu-candidate` layout, re-runs `Verify-NativeFeed.ps1`, then
restores with an empty package cache, builds, formats, tests, packs and uploads
`release-packages-<sha>-<os>`. The `publish` job downloads only the
`ubuntu-latest` package set, re-runs `ReleasePackageSet.ps1 -Mode Verify` from
a fresh checkout, and only then requests the OIDC key behind `environment:
nuget`.

## Negative and positive rehearsal coverage (local)

`local/feed-rehearsal-02.json` (executed 2026-10-26T09:43:49Z against the
production verifier and the staged feed):

| Scenario | Expected | Observed | Verifier message (abridged) |
| --- | --- | --- | --- |
| R15a valid feed | PASS | PASS | `NATIVE FEED VERIFIED: 7 packages at 0.2.0-preview.10, source bf33c05..., Penghou.Workflow.Abstractions [0.1.0-preview.2]` |
| R15b feed absent | REFUSED | REFUSED | `Native feed directory is absent` |
| R15c package missing | REFUSED | REFUSED | `Feed contains 6 nupkg files; exactly 7 are required` |
| R15d wrong version | REFUSED | REFUSED | `Pin version '0.2.0-preview.10' does not match expected native version '0.2.0-preview.9'` |
| R15e commit mismatch | REFUSED | REFUSED | `Resolved native source commit '0123...' is not the pinned commit 'bf33c05...'` |
| R15f invalid pin | REFUSED | REFUSED | `Pin file is invalid: SourceCommit must be a 40-character commit SHA` |
| R09 extra package | REFUSED | REFUSED | `Feed contains 8 nupkg files; exactly 7 are required` |
| R15g corrupt bytes | REFUSED | REFUSED | `Rebuild hash mismatch: ...Penghou.Zhinu.Hosting...nupkg` |
| R11 native dependency pin (targeted) | REFUSED | REFUSED | `Native core package declares 'Penghou.Workflow.Abstractions' versions '[1.0.0]'; the accepted pin is '[0.1.0-preview.2]' (R11 REFUSE)` |
| R16 absent-feed restore | REFUSED | REFUSED | `NU1101 ... source(s): zhinu-candidate ... nuget.org not considered` |
| R18 fresh-cache restore | RELEASE-ELIGIBLE (NO PUSH) | RELEASE-ELIGIBLE (NO PUSH) | `Restored ... AdapterProbe.csproj` |

The R11 row is a targeted fixture recorded in
`../Penghou.Fuwen/docs/evidence/zg-rq-02/local/feed-r11.json`: the core
package's dependency was changed to `[1.0.0]`, the rebuild-manifest hash was
updated so the run reaches the dependency check, and the production verifier
refused with the R11 message. The Fuwen harness header lists R11, but the
09:43 matrix did not include it; this targeted case closes that coverage gap
without rewriting the historical matrix.

The first matrix (`local/feed-rehearsal-01.json`) is retained as **invalid
negative evidence**: the harness accessed an absent optional key under
`Set-StrictMode`, so several "refusals" were harness exceptions. It was
corrected (key-presence check plus expected-message assertions) and the second
matrix is the valid record. See `observations.md`.

The worktree's staged feed was rebuilt after the 09:43 matrix for the
native-equivalence comparison, so the current `artifacts/zhinu-candidate` no
longer pairs with the 09:43 manifest. The matrix JSON remains the historical
record of the 09:43 feed/manifest pairing; the current feed pairs with
`../Penghou.Fuwen/docs/evidence/zg-rq-02/local/native-rebuild-02/zhinu-source-candidate-a0ed614f75a44df4be114a0e7eb0d696.json`.
The R11 fixture was executed against that current pairing. The hosted
`prepare-native-feed` job builds and verifies its own feed/manifest in the
runner, so this local workspace detail does not affect the hosted evidence.

## Hosted evidence

Rehearsal run `40308636700` at `6565822`:

- `prepare-native-feed`: **SUCCESS** — pinned source checkout, seven-package
  source build, preparation rejection boundaries, feed verification, upload.
- `validate (ubuntu-latest)`: **SUCCESS** — downloaded and re-verified the
  staged feed, empty-cache restore, build, format, full tests, canonical JSON
  vector, pack, package-set binding, upload.
- `validate (windows-latest)`: **FAILED at Test** with the REL-SQLITE-01
  recurrence (`SQLite Error 10: 'disk I/O error'` at `lease.recover`, blocked
  item "b"). The `handoff` and `authorization-probe` jobs were skipped; no
  publication path ran. This is fail-closed behavior. The failure is retained
  verbatim in `local/hosted-rehearsal-40308636700-windows-failure.log` and
  classified in `stress-qualification.md`.

Earlier hosted failures are retained, not erased: `40302339008` (hosted fixture
path defect), `40302665778` (hosted formatting gate) and `40303275879`
(workflow-contract test pinned the old single-file publisher, fixed in
`6565822`).

## Provenance limits

The staged feed is a **per-run source rebuild**, not the frozen Windows archive
set and not a public-feed substitute. The seven staged nupkg hashes therefore
differ from the pin's recorded archive hashes; the binding contract is
identity/version/dependency/source-commit plus the per-run rebuild manifest
hash, as recorded in the release packet. `artifact-equivalence.md` and
`local/native-equivalence.json` record payload equivalence for two local
rebuilds (byte-identical decompressed members, different ZIP timestamps). Do not
claim byte identity to the original archives or to a public release.

## Limits

- The publisher was exercised by the nonpublishing rehearsal of the same
  reusable workflow; no tag was pushed and no package was published.
- The hosted Windows validation cannot be green while REL-SQLITE-01 remains
  open; the gate correctly refuses rather than bypassing the failure.
- The native source checkout is a cross-repository read of the pinned commit;
  if that commit disappears from the Zhinu repository, the job fails closed.
