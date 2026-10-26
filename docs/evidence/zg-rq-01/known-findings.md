# Q7: known findings disposition

## REL-SQLITE-01 — Windows SQLite I/O failure after worker termination

Status after this qualification: **open; recurrence response now due.**

1. **Classification read:** the finding is recorded open with cause
   unestablished (Fuwen `docs/reliability/windows-sqlite-process-loss.md`),
   explicitly separate from the fixed integration defects and from the
   preview.20 publication decision; a successful rerun does not close it.
2. **Signature match confirmed:** the 2026-10-25 occurrence in Fuwen CI run
   [40187614750](https://github.com/jenolaszlo-sketch/penghou-fuwen/actions/runs/40187614750)
   (attempt 1, windows-latest, .NET 10) failed
   `Explicit_selection_process_loss_retires_partial_history_and_recovers_durable_replacement(blockedItem: "b")`
   with `Microsoft.Data.Sqlite.SqliteException (0x80004005): SQLite Error 10:
   'disk I/O error'` raised from
   `SqliteLeaseRepository.RecoverExpiredLeasesAsync` after killing and
   awaiting the worker — the same test, operation and error code as the
   documented original incident.
3. **Rerun verified:** the unchanged-source rerun of the failed Windows job
   and its dependent consumer job passed (all four platform jobs green); CI
   logs are retained by GitHub with the run above.
4. **Occurrence count and environments:** two documented CI occurrences of the
   exact signature — 2026-10-20 (Fuwen integration CI
   [39408318656](https://github.com/jenolaszlo-sketch/penghou-fuwen/actions/runs/39408318656),
   attempt 1) and 2026-10-25 (run above, attempt 1) — plus the recorded local
   Windows rechecks and green CI passes in between. Environment:
   GitHub windows-latest runners, .NET 8/10, Microsoft.Data.Sqlite with the
   recorded WAL/pooling settings, SQLite error 10 at lease recovery after
   process termination.
5. **Recurrence threshold:** the recorded recurrence response (retain source
   and archives, capture the full diagnostic chain, preserve the database
   copy before cleanup, then the bounded 64-execution Windows stress batch
   over the blocked-item `a`/`b` cases) is explicitly "triggered by recurrence
   or a separate request". The 2026-10-25 event **is that recurrence**: the
   response is now due and has not been executed. It is owner-scope work and
   was not performed by this qualification.
6. **Preservation:** the finding remains open with both occurrences retained;
   the original failure is not erased by any rerun.

**Blocking status:** non-blocking for the candidate pair under the recorded
release policy (the release packet explicitly keeps it separate from the
publication decision), but the due recurrence-response work must be scheduled,
and any further comparable failure must trigger the recorded retention
procedure before cleanup.

## ZG-RQ-01-OB-01 — transient `database or disk is full` during seed probe
(new observation, recorded 2026-10-25)

During Q1 fixture preparation (see
[compatibility.md](compatibility.md)), seed attempt 1 failed at its first
`run.create` commit with `Microsoft.Data.Sqlite.SqliteException (0x80004005):
SQLite Error 13: 'database or disk is full'` on a **fresh** database, while the
volume had ~3.4 GB free (verified immediately after). The failing attempt is
preserved in the qualification logs; the rerun and every subsequent seed
execution (five further runs) passed identically.

- Severity: observation only. The failure occurred inside the **published
  preview.4** packages, not the candidate, so it cannot implicate the
  candidate; it is environmental (Windows SQLite storage-I/O anomaly family).
- Relationship to REL-SQLITE-01: same family (unexplained Windows SQLite
  I/O error), different SQLite error code (13 vs 10), different operation
  (first insert commit vs lease recovery after process kill) and different
  package vintage. It does not match the REL-SQLITE-01 signature exactly and
  therefore does not count toward that finding's recurrence threshold, but it
  should be considered by the Windows-I/O reliability follow-up alongside the
  stress batch.
- Disposition: non-blocking; retained as environmental evidence with the
  passing reruns.

## Probe-harness notes (not product findings)

- A schema-rejected `SqliteWorkflowStore` retains its pooled SQLite handles
  until process exit; the rejection probe therefore hashes the future-schema
  database copy with sharing-tolerant reads. The rejection semantics
  (typed `ZhinuSchemaCompatibilityException`, unhealthy readiness report,
  unchanged database bytes) are unaffected.
- The `SqliteWorkflowStore` public surface exposes no standalone disposal;
  disposal is engine-owned by design. Probe code disposes the owning engine.
