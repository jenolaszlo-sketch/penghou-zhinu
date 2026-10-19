# ZG-6.1 — Fuwen static integration and grouped signal waits

Date: 2026-10-19. Status: **passed for the bounded Windows static profile**,
.NET 8/10. Runtime/package source:
`fd1875886e67e3aacd9c03c5689238788575d1e5`; unpublished candidate
`0.2.0-preview.5`. [Evidence ledger](zg6-static.json).

Fuwen's first real macro/logical-group integration consumes the exact seven
candidate packages. Its source checkpoint is
`cab1d71cbce67f75b9cf5711403061fa8daeea67`, with its own qualification record and
capability profile. Fuwen core remains independent of Zhinu; no Fuwen/compiler
dependency enters Zhinu core.

The integration exposed one missing native seam: signal waits could not carry
explicit group membership. `WaitForGroupedSignalAsync<T>` adds that capability
using the existing wait claim path. The separate method name preserves existing
overload resolution, including calls passing a null timeout. The old overload
delegates to the same core with no membership. Foreign-run bindings fail before
a wait step is created. Wait cancellation/reopen/signal completion preserves the
step ID, revision and direct occurrence membership, and queries report Waiting
work while the signal is pending. No wait, lease, authority, schema or occurrence
identity algorithm is redesigned.

Qualification passes **1,901 tests**, 0 failures/skips, in 11 full-suite TRX
receipts. The two new tests run on both frameworks (four new executions).
Release build has zero warnings/errors and formatting verification passes.
Seven packages and 14 packaged binaries match the source build. Both package-only
and precompiled legacy-consumer probes pass on .NET 8/10, including old schema5
retention, schema5→9 migration and old-worker rejection on schema9. The ledger
records source/package/binary/test hashes and both probe receipts.

Fuwen additionally qualifies static nested/repeated macro invocations, parentage,
Work/Control membership, public step mapping and hard process-loss recovery at
a grouped wait. Recovery loads pinned expansion bytes and does not need source
or a macro library; committed fake-provider work executes once across launches.
Its 1,652 regression executions and eight package-only consumer executions are
separate from the native count above.

Full ZG-6/FM-2 and ZG-7 remain open. Fuwen grouped repeat/fan-out per-child
lowering, grouped coordinated inference, dynamic closure evidence, broader macro
parameters and cross-platform/public dependency qualification are not established
by this checkpoint. Historical item boundaries and keys remain unchanged.
No packages have been published.
