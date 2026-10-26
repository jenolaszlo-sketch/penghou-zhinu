# Artifact and source equivalence

Verdict: **semantically equivalent, not byte-identical.** Source is identical;
the hosted Stage-A artifacts have identical assembly types, references and
method IL to the recorded candidate set, while archive bytes, MVIDs, PDB
checksums, XML line endings and the nuspec repository branch/commit binding
differ by build environment. Raw record: `local/artifact-equivalence.json`.

## Source equivalence

| Comparison | Result |
| --- | --- |
| `git diff f0edf17 bf33c05 -- src tests` (Zhinu baseline vs Fuwen's pinned native commit) | empty |
| `git diff f0edf17 <implementation> -- src tests Directory.Build.props NuGet.config` | empty (`scope-check.json`) |
| Full `f0edf17..bf33c05` delta | only `README.md`, `ROADMAP.md`, `docs/**` and `eng/Verify-BranchReplacementPackages.ps1` |

The Fuwen native pin `bf33c05` therefore carries the same production source and
tests as the ZG-RQ-01 qualified baseline `f0edf17`; Q1-Q5 inheritance is not
resting on a different source tree. Only release automation, verification
scripts, tests (diagnostic-only, env-gated), release documentation and evidence
changed in the implementation branches.

## Artifact comparison

Baseline: the recorded ZG-RQ-01 candidate set built from `f0edf17`
(`C:\Users\Laszlos\source\repos\Penghou.Zhinu\artifacts`).
Candidate: the GitHub-hosted Stage-A artifact set produced by CI run
`40303504133` at `908555d` (source-identical to `f0edf17`), artifact
`release-qualification-ubuntu-latest`.

| Property | Result |
| --- | --- |
| Archives compared | 14 (7 nupkg + 7 snupkg) |
| Archive SHA-256 identical | 0 / 14 |
| Nuspec package ID/version identical | 14 / 14 |
| Nuspec dependency pins identical | 14 / 14 |
| Nuspec difference | repository branch/commit only (`refs/heads/main` + `f0edf17` vs `refs/heads/codex/zg-rq-02-zhinu` + `908555d`) |
| nupkg XML documentation, normalized line endings | 7 / 7 identical |
| Assemblies compared (net8.0 + net10.0) | 14 |
| Assembly type definitions identical | 14 / 14 |
| Assembly references identical | 14 / 14 |
| Method IL identical | 14 / 14 |
| MVID | differs (expected across independent builds) |
| PDB checksum | differs (expected; PDBs also embed MVID/paths) |
| ZIP member bytes | differ for every member (timestamps/compression/line endings), payload semantics as above |

Example: `Penghou.Zhinu.0.2.0-preview.10.nupkg`
baseline `b7b8d7eac1d8d9519700a887adf01079f94de1dbd91b4c9c2bc9ea37410059a3`
(706264 bytes) vs candidate
`c47b8a1497fa352599997369d5309f6d31d92602df2f3b947a056c9e67b7fb4b`
(705944 bytes). The difference is ZIP metadata/compression and the nuspec
repository binding, not assembly IL.

## Method

- `src/` and `tests/` diffs were run with `git diff --name-status`; an empty
  result is the source-equivalence proof.
- Each archive was opened as a ZIP; every member was SHA-256 hashed. Members
  were compared by name and content.
- Each nupkg's `.nuspec` was parsed for ID, version, dependency groups and
  repository metadata.
- Each managed DLL was read with `System.Reflection.Metadata`: module MVID,
  type definitions, assembly references and per-method IL hashes were compared.
- XML documentation was compared after CRLF-to-LF normalization (the hosted
  Linux build emits LF; the recorded Windows build emitted CRLF).
- Scripts and raw outputs are retained under `tools/` and `local/`.

## Limits

- This is **not** a claim of reproducible archive bytes. ZIP timestamps,
  compression choices, MVIDs and PDB checksums differ between independent
  builds and platforms. The defensible claim is source identity plus
  assembly-level semantic identity.
- The baseline set was built locally on Windows; the candidate set was built on
  a GitHub-hosted Ubuntu runner. Cross-platform PDBs and XML line endings are
  expected to differ.
- PDBs are not IL-compared; they are debugging artifacts whose bytes embed the
  MVID and source paths. Their presence and package identity are verified, not
  their byte identity.
- No package was published, so no external consumer has yet restored these
  exact archives; the restore evidence in `rg-01-zhinu.md` and
  `rg-03-fuwen.md` is from the staged/local feeds.
- Fuwen's native rebuild equivalence is recorded separately in
  `../Penghou.Fuwen/docs/evidence/zg-rq-02/artifact-equivalence.md` and
  `local/native-equivalence.json`: two local rebuilds were payload-identical
  with different ZIP timestamps; that is a different comparison from this
  Zhinu baseline-vs-hosted comparison.
