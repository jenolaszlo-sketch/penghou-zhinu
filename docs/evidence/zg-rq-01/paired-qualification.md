# Q5: Zhinu / Fuwen paired compatibility (local candidate pair)

Verdict: **PASS for the local candidate pair.** The public pair is not claimed:
native preview.10 and Fuwen preview.20 are both absent from the public feed at
the recorded observation, and public-pair qualification is a recorded
post-publication step.

## Q5.1 binding and dependency direction

- Fuwen `0.1.0-preview.20` (source `8c7da4cfbc882804d801fe0e8b073733e4ee563c`)
  references exactly `Penghou.Zhinu [0.2.0-preview.10]` and
  `Penghou.Zhinu.Sqlite [0.2.0-preview.10]` in its adapter and consumer test
  projects, with the native pin recorded in Fuwen
  `docs/qualification/activity-groups/zhinu-candidate.json`
  (native source `bf33c05cf2390af6924bbe3761e09f9e377c7a97`, schema 10,
  original archive hashes preserved).
- **Native baseline equivalence:** `git diff bf33c05 f0edf17` over `src/` and
  `tests/` is empty (documentation and one qualification script differ only).
  Every bf33c05-bound paired qualification therefore applies to the bound
  candidate, and the Fuwen CI re-proves the pair at the pinned source on every
  run.

## Fresh paired evidence at the pair (2026-10-25/26)

- Fuwen CI at the merged Fuwen main tip `8c7da4c` (workflow-dispatched run
  [40187614750](https://github.com/jenolaszlo-sketch/penghou-fuwen/actions/runs/40187614750)):
  all four platform jobs green (build-test-pack and consumer-proof on
  ubuntu-latest and windows-latest). The workflow checks out the pinned native
  source, packs it into an isolated local feed and restores Fuwen against the
  candidate packages package-only — 1,770 regression executions and 32
  package-only consumer executions per OS, plus the candidate-preparation
  rejection checks. One Windows job attempt failed with the REL-SQLITE-01
  transient and passed on the recorded rerun (see
  [known-findings.md](known-findings.md)); the unchanged-source rerun does not
  close that finding.
- The sealed integration review
  (Fuwen `docs/qualification/activity-groups/integration-second-pass.md`,
  runtime `eca1213796d9fa4ad08423e2691cb409c892f63b` = the preview.20 runtime
  source, native `bf33c05`) qualifies the plan-to-execution mapping, package
  restoration and consumer proofs on both frameworks and OSes.

## Q5.2 plan-to-execution mapping

Covered by the sealed review and the green Fuwen CI at the pair: sequential
execution, static groups/macros, single-level grouped repeat, one fixed keyed
fan-out with independently durable item providers, static
conditions/checkpoints/waits, branching and explicit bounded selection with
native replacement, failed activities and retries, and the stable
step/occurrence identities asserted by the mapper tests
(`FuwenZhinuStepMapperTests`, `FuwenZhinuGroupTests`,
`FuwenZhinuGroupRepeatTests`, `FuwenZhinuSelectionTests`,
`FuwenZhinuIntegrationReviewTests` and related). Unsupported combinations
(nested/mixed dynamic DSL scopes, evolving selection, item
waits/checkpoints/merges, grouped coordinated inference, Fuwen child syntax)
remain negative preflight cases, not silently flattened passes.

## Q5.3 paired restart

Process-level paired recovery is qualified by the ZG-7 Q44/Q45 records (nested
macros inside repeated iterations and the two-operation keyed body killed
after the first operation commits; recovery preserves boundaries, external
effect receipts, occurrence parents and mapper coverage, with no source
compilation or macro-library lookup on the recovery worker) and by the Fuwen
`GroupProcessProbe` execution-loss cases in the green CI run above. The
recovery worker receives only immutable bundle bytes and the host catalogue;
plan-to-step identity, group membership and result binding are asserted
against durable state after restart.

## Q5.4 release ordering

The recorded order is **native Zhinu first, then Fuwen, then Guihua, then
applications**, with public indexing and fresh-cache public-pair
qualification at each dependency boundary. The local pair above satisfies the
pre-publication step of that procedure; the remaining steps are conditional on
owner-authorized publication and are not claimed here. The Fuwen tag
workflow's native-feed defect that currently blocks the Fuwen release path is
recorded as finding RG-03 in [release-gates.md](release-gates.md).
