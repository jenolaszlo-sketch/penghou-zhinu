# Q1 + Q3: persistence, schema and consumer compatibility

All re-executions bound to Zhinu `f0edf1714013106bdbce090179342ccb029eaf83`
(candidate, schema 10). Windows 11 x64, .NET 8.0.x / 10.0.x, 2026-10-26.
Existing records are cited with their own source bindings.

## Q1.1/Q1.2 — cross-version fixture upgrade and restart recovery

**Method.** Two separate console processes (a seed and a recovery process), so
the upgrade boundary is a real process boundary, not an in-process reopen:

1. **Seed process** restores the **published** `Penghou.Zhinu` /
   `Penghou.Zhinu.Sqlite` `0.2.0-preview.4` packages from nuget.org only
   (cleared sources, fresh package cache), creates a fresh database (schema 7)
   and five workflows: a completed two-step run with metadata, a failed run, a
   run parked on a durable delay wait, a run parked on a durable signal wait,
   and a never-executed pending run. It checkpoints WAL, records the database
   SHA-256 and exits.
2. **Recovery process** restores the **candidate** `0.2.0-preview.10` packages
   from the local candidate feed only (package-source mapped; nuget.org for
   non-Penghou dependencies), opens the same database, and verifies migration
   and preservation, then recovers every run to a terminal state.

**Observed (final continuous run, 2026-10-26):**

```text
seed exit: 0
two=Completed, fail=Failed, delay=Running(Delay:Parked), signal=Running(Signal:Parked), pending=Pending
two-steps=first:Completed:a1, second:Completed:a1
fixture sha256 (after seed, schema 7): a27e752267fac181612942f71915244f481e430eb49d6e44681021bc26860aa4

recover exit: 0
schema-marker-after-migration=10
preserved: two=Completed, fail=Failed, delay=Running, signal=Running, pending=Pending
preserved: two-steps=first:Completed:a1, second:Completed:a1
preserved: two-result=two:done:q1
preserved: delay-waits=1:Delay:Parked
preserved: signal-waits=1:Signal:Parked
preserved: pending-steps=0
replay: two-steps=first:Completed:a1, second:Completed:a1  (no duplicate attempts)
final: two=two:done:q1, pending=pending:done:q1,
       signal=signal:done:q1:granted, delay=delay:done:q1, fail=Failed
```

Run identities, statuses, step identities/attempts, results, metadata and both
parked wait kinds survive the 7-to-10 migration; the pending run completes on
recovery, the signal run is woken by a signal, the delay run completes when its
persisted deadline is due, and re-executing the completed run performs no
additional step attempt. Probe sources and both processes' console receipts
are retained under the qualification scratch directory (`zgrq01`, see
known-findings for the observed transient on one seed attempt).

**Attempt history (preserved per evidence-integrity rules):** seed attempt 1
(2026-10-25, database `zg-rq01.db`) hit `SQLite Error 13: 'database or disk is
full'` during `run.create` on a fresh database with ~3.4 GB free on the volume;
see [known-findings.md](known-findings.md) (ZG-RQ-01-OB-01). The rerun and all
subsequent seed executions passed; the recorded final continuous run above is a
complete fresh seed followed by the recovery process.

## Q1.3 — schema rejection

Future schema: after recovery, a copy of the migrated database had its
`zhinu_schema` marker set to 11. Observed at the candidate:

```text
q1.3-future-schema-rejection: PASS
rejectedByOperation=true  (Penghou.Zhinu.Sqlite.ZhinuSchemaCompatibilityException)
rejectedByHealth=true     (readiness probe reports unhealthy)
rejectionDetail="SQLite database schema 11 is incompatible with runtime schema 10. Recreate the preview database."
bytesUnchanged=true
```

Unsupported future versions fail closed with the typed public exception, the
readiness probe reports unhealthy, and the database bytes are unchanged by the
rejected open. Old-worker direction (existing evidence, bound to
`bf33c05`, source-identical to the candidate): the Q33 record proves the
unchanged published preview.9 worker rejects the schema-10 database without
changing database bytes, with real schema-9 state created by actual preview.9
packages ([q33.md](../../qualification/activity-groups/q33.md)). Downgrade is
not promised by the existing contract and was not qualified.

## Q1.4 — supported chain and fixture provenance

| Hop | Evidence |
| --- | --- |
| 5 -> 6, 6 -> 7 | In-suite `WorkflowAuthorizationMigrationTests` / `AuthoritySubjectBindingTests` (green at the candidate, both frameworks); release records for preview.1/preview.4 |
| 7 -> 8 | ZG-2+3a record: published schema-5 rows upgraded to 8 and a separate v7 migration retaining legacy run/step/authority with a successful grouped claim after migration ([zg2-3a.md](../../qualification/activity-groups/zg2-3a.md)) |
| 8 -> 9, 9 -> 10 | ZG-3b/ZG-7 records; Q33 record: schema 5-10 migration and older-worker rejection pass locally and in both CI jobs; schema-9 migration exercised with actual selected work created by actual preview.9 packages ([zg7.md](../../qualification/activity-groups/zg7.md), [q33.md](../../qualification/activity-groups/q33.md)) |
| 7 -> 10 direct (published-to-candidate) | The cross-version probe above, fixture created by the published preview.4 package set from nuget.org |

Historical fixtures were produced by the actual historical package binaries
(nuget.org restore with a cleared, fresh cache), not reconstructed from current
assumptions; fixture hashes are recorded above and the fixture database was not
modified in place (the rejection probe operates on a copy).

## Q3.1 — legacy binary probes

- `Verify-LegacyWorkflowConsumer.ps1` at the candidate (local Windows,
  2026-10-26): **PASS** — the precompiled consumer built against published
  `0.1.0-preview.15` binaries runs unchanged against the candidate packages on
  both frameworks, including the compatibility overload that repaired the
  AE-6.2 start-signature regression. CI re-runs the same probe on both OS jobs
  at `f0edf17` ([run 40190759599](https://github.com/jenolaszlo-sketch/penghou-zhinu/actions/runs/40190759599)).
- Existing record: the precompiled **public** Fuwen preview.11 consumer runs
  unchanged against the candidate runtime on a fresh schema-9 database
  ([zg7.md](../../qualification/activity-groups/zg7.md)).

## Q3.2 — public API comparison against the published preview.4

Compared the union of each package's public API baselines at the published
preview.4 source (`0a523826c2d7281164ee0b1826fcb1bbee2d6689`, the exact commit
published as 0.2.0-preview.4) against the candidate `f0edf17`:

| Package | Published preview.4 members | Candidate members | Removed/changed | Added |
| --- | --- | --- | --- | --- |
| Penghou.Zhinu | 1870 | 2593 | **0** | 723 |
| Penghou.Zhinu.Sqlite | 191 | 203 | 1 | 13 |
| Penghou.Zhinu.Hosting | 10 | 10 | 0 | 0 |
| Penghou.Zhinu.Hosting.AspNetCore | 2 | 2 | 0 | 0 |
| Penghou.Zhinu.Agents | 19 | 19 | 0 | 0 |
| Penghou.Zhinu.Testing | 53 | 53 | 0 | 0 |
| Penghou.Zhinu.OpenTelemetry | 2 | 2 | 0 | 0 |

Classification: the core surface is purely additive. The single Sqlite change
is `const ZhinuSqliteSchema.CurrentVersion = 7 -> 10`, the intentional
version-defining schema marker (constants are inlined into existing binaries,
so no binary break; the compatibility contract is the migration chain, not the
constant value). No removed methods, types, accessibility or generic-constraint
changes; serialized contract shapes are unchanged for existing rows (verified
by the Q1 migration probe). The added members include the legacy-compatibility
start overloads, the group APIs, and the M5 read surfaces.

## Q3.3 — dependency compatibility

Packed candidate `Penghou.Zhinu` nuspec declares exactly:
`Penghou.Workflow.Abstractions [0.1.0-preview.2]` and
`Microsoft.Extensions.Logging.Abstractions 10.0.9` on both TFMs. The exact
preview.2 pin was explicitly qualified as compatibility evidence during the
neutral contract's 1.0.0 graduation; adopting 1.0.0 remains a deliberate future
release decision. `Verify-WorkflowPackageClosure.ps1` passes at the candidate
(fresh cache, exact candidate closure, no project references, no Hufu
dependency). No unintended prerelease or local-only dependencies resolve.
