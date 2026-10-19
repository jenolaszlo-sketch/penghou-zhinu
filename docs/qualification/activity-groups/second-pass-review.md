# Activity groups: second correctness pass

Date: 2026-10-20. Scope: the activity-group branches through native runtime
`246d535679cca2ca5130ecfad62437dbb1fdd81d` and Fuwen runtime
`535f82e2bd70d9a163b27be8671d82c75084a0dd`. Final platform evidence is recorded
separately in `zg7.json`. Original checkpoints retain their own source and hashes.

## Confirmed findings and corrections

1. **Completed fan-out items lost their independent completion state (P2).**
   A completed multi-operation item appeared Pending while its sibling ran and
   Cancelled after run cancellation. Native preview.9 derives a snapshot-time
   item seal from the current selection, input identity, dependencies and exact
   completed Work membership. Nested static groups share that seal; parents
   remain open. Missing optional provider hashes remain conservative. Native
   constructor ABI, schema 9, identities and scheduling remain unchanged. The
   Fuwen progress test first failed against preview.8 and passed against preview.9.

2. **Admission allowed a grouped item path without durable work (P2).**
   Logical groups may omit a result export. A conditional inside such a group
   could skip all provider operations, pass registration, then fail native scope
   sealing. Fuwen preview.18 rejects that shape before definition storage. An
   unconditional provider, or a conditional with guaranteed work in both branches,
   satisfies the structural guarantee recursively. Separate conditions are not
   analyzed for exhaustiveness. A failing admission regression was retained;
   the corrected negative case and both-branch positive control pass on .NET 8/10.
   Historical ungrouped fan-out and supported native body execution are unchanged.

3. **The new legacy probe assumed Windows TEMP (P2 for qualification).**
   It now uses `Path.GetTempPath()`. The unchanged public Fuwen preview.11 binary
   is tested independently from database migration: its schema-4 database is
   rejected unchanged, while a fresh candidate database proves binary execution.
   Supported schema 5–9 migration remains a separate native legacy probe.

4. **Formatter line endings differed by OS (P2 for qualification).**
   Git required LF, but Fuwen had no formatter setting for it. Windows CI failed
   while Linux passed. A repository `.editorconfig` now requires LF for C#;
   the formatter check remains enabled on both OS jobs. The failed run is retained.

5. **Two new wait tests had unbounded polling.**
   Approval and changed-pin tests now use linked 30-second deadlines throughout
   their native operations and polling. Their three cases pass on both frameworks.

6. **Current-status documentation contradicted later evidence.**
   The native roadmap still called queries/progress unimplemented despite checked
   ZG-4/5 checkpoints; migration guidance called preview.15 current. Current entry
   points now point to the consolidated profile. Historical qualification records
   remain dated and retain their original exclusions and archive identities.

7. **The privacy assertion used incompatible Windows file sharing.**
   An existing file-byte assertion retried `ReadAllBytes` while SQLite could still
   hold a write handle. It now reads with `ReadWrite | Delete` sharing and checks
   WAL bytes as well as the main database; the payload exclusion assertion is
   preserved and strengthened. The privacy case and both wait-recovery cases
   pass locally on both frameworks.

8. **Remote qualification needed storage isolation and hang diagnostics.**
   Windows CI exposed a SQLite disk-I/O error after hard process loss, and the
   original native run did not finish its SQLite suite before interruption. New CI uses `RUNNER_TEMP` for all temporary database
   directories (D: on the Windows runner), matching the local D: qualification.
   A five-minute VSTest hang diagnostic retains the active-test sequence instead
   of waiting indefinitely. These are harness changes, not a runtime retry or
   suppression of SQLite errors. Original failed/interrupted runs remain
   evidence. Corrected native and Fuwen producer/consumer jobs pass on both OS;
   no SQLite error is retried or suppressed by the harness.

9. **Fresh consumers inherited incompatible NuGet source mappings.**
   Both producer jobs passed, but external consumers could not resolve even test
   dependencies because their feed names differed from inherited mappings. Each
   consumer configuration now clears inherited mappings and explicitly maps
   Fuwen/native candidates and public dependencies. Both default configurations
   pass all 26 tests using an empty cache; raw assets and supplied/cached archive
   hashes are retained. Warm-cache or explicit-config success was kept separate.

10. **Full Q33 must not be marked passed from narrower history tests.**
    The native ZG-5 record explicitly makes no generic branch-selection claim.
    Superseded fan-out history and conservative Partial/Unknown projection are
    tested, but replacing a controlling branch and retiring its old pending
    membership remains unqualified. The consolidated ledger marks Q33 partial;
    selected-profile completion does not close the whole original matrix.

## Additional checks and efficiency

The pass inspected immutable bundle/manifest admission, direct innermost
membership, typed keyed/iteration coordinates and parent prefixes, run-local fork
rebinding, selected-item claim and lease fences, dependent restart and closure,
byte-only process recovery, snapshot authorization/cache bounds, Work/Control
reduction, mapper key round trips, package closure and precompiled consumers.
Native core has no Fuwen compiler dependency or macro-name dispatch.
The prior-artifact audit checked 269 native and 70 Fuwen recorded artifact hashes;
all 339 match, with no missing records among those checked.

The per-item seal is prepared once per immutable snapshot using shared indexes;
queries use a set lookup. No per-query seal cache or identity redesign was added.
Candidate source preparation builds only the seven shipped native projects,
avoiding unrelated solution test/benchmark restores. Rebuilt archives have their
own provenance and hashes; neither their bytes nor their package versions are
silently substituted for the original local archive pin.

No additional confirmed runtime defect remained in the selected profile after
these corrections. Broader dynamic composition, evolving-selection DSL lowering,
item waits/checkpoints/merges, grouped coordinated inference/fallback and group
control APIs remain explicit future work, with supported adapter exclusions
rejected at preflight. Release publication and downstream application adoption
are separate gates.
