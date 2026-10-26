# Preserved failures and qualification limits

1. Recovered Fuwen fixture used `Parameters.ResolvedSourceCommit` under strict
   mode even when absent. The first local matrix incorrectly counted that
   harness exception as a rejection. `local/feed-rehearsal-01.json` is retained
   as **INVALID negative evidence**, not a production pass/failure. Corrected
   code checks key presence and the expected rejection message. The second
   matrix proves the real verifier handles the intended faults. Its positive
   fresh-cache restore succeeds and the absent-feed case returns NU1101,
   explicitly excluding nuget.org for the mapped native package.
2. New offline API mock initially used script-scoped variables that resolved in
   the called verifier script; the second attempt omitted the real API's `name`
   property. `api-offline-01/02` are preserved failed harness observations.
   Explicit fixture state and the completed response shape yield 19 passing
   scenarios in `api-offline-03`. No live API refusal is inferred from these.
3. Fuwen hosted run 40302339008 failed its positive clean-restore fixture after
   successful native source build, rejection probes and production feed checks.
   Its table truncated the exact restore error; initial workflow omitted an
   always-upload of that fixture receipt. The next change used portable slash
   paths and retained full errors and receipts. Run 40302665778 passed native
   preparation/fixture checks, so the first observation is a harness/platform
   path defect, with its exact original error text unavailable. The failed run
   and original log are preserved; do not claim an independently established
   deeper NuGet/runtime root cause.
4. Run 40302665778 then failed the mandatory formatting gate at the diagnostic
   initializer (two properties on one line). Local SDK 10.0.401 check had passed;
   hosted SDK 10.0.403 requested a newline. Commit 5ec2479 applies only that
   whitespace correction. This is a harness formatting defect, not production
   SQLite failure. No test or format gate was removed or relaxed.

5. The Zhinu rehearsal workflow header originally claimed that the hosted run
   exercised live-API negatives against historical runs. The hosted step is
   explicitly an **offline** API-shape/tag fixture suite; the live-API negatives
   were executed separately, read-only, against real historical runs and are
   recorded in `local/live-api-negatives.json`. Commit `a4bdfa5` corrects the
   header comment; the hosted rehearsal was re-run at that commit. The earlier
   hosted run `40308720683` remains valid evidence for the verification and
   environment path, but not for live-API coverage.
6. Fuwen hosted rehearsal `40308636700` attempt 1 reproduced REL-SQLITE-01 on
   windows-latest during the existing blocked-item "b" process-loss test
   (`SQLite Error 10: 'disk I/O error'` at `lease.recover`). The failure log and
   run/job metadata were copied into `local/` before any rerun; the rerun is a
   separate attempt and does not erase attempt 1. The failed qualification
   skipped handoff and the environment checkpoint, which is the intended
   fail-closed direction.
7. The first Zhinu baseline-vs-Stage-A artifact comparison reported every
   member as different because archive bytes, MVIDs, PDB checksums and XML line
   endings differ across build environments. That was a **method observation**,
   not a product regression. The corrected comparison normalizes XML line
   endings and compares assembly types, references and per-method IL; it finds
   14/14 assemblies semantically identical. The raw record and method are in
   `local/artifact-equivalence.json` and `artifact-equivalence.md`.
8. The Fuwen worktree's staged feed was rebuilt after the 09:43 matrix for the
   native-equivalence comparison, so the current `artifacts/zhinu-candidate`
   does not pair with the 09:43 manifest. The matrix JSON remains the historical
   record of that run; the current feed pairs with the preserved
   `native-rebuild-02/zhinu-source-candidate-a0ed614f...json`. The targeted R11
   fixture uses the current pairing, and the hosted prepare-native-feed job
   builds/verifies its own feed in the runner. Do not use the current worktree
   feed as if it were the 09:43 matrix input without re-pairing the manifest.

9. Some raw API captures in `local/` were initially written in UTF-16LE by the
   capture shell. They were normalized to UTF-8 (content unchanged) so reviewers
   and tooling can read them; no recorded values were edited.
10. The independent adversarial review (see `independent-review.md`) corrected
    one provenance overclaim: `eng/test_workflow_boundaries.py` is executed
    locally, not in the hosted rehearsals. The affected sentences in
    `rg-01-zhinu.md` and `rehearsal-matrix.md` were corrected; the test's own
    output files already stated "STATIC WORKFLOW MUTATION; NOT HOSTED
    ENFORCEMENT".

All corrected attempts have distinct commits/run IDs or fresh fixture output
directories. None overwrites a failure, creates a tag, or publishes a package.
