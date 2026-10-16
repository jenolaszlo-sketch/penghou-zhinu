# ZG-3b.1 qualification: root native fan-out

Date: 2026-10-16. Status: **passed for the root fixed-selection native profile**.
Runtime/package source: `e4b7f139b93c3934860ed5a3f7aae9225d3f5ba3`.
The commit containing this record adds qualification evidence only. Exact test
receipts, package/source hashes, recovery records and binary/package results are
retained in [zg3b1.json](zg3b1.json).

Branch: `codex/zhinu-activity-groups`. Documentation base: `91cc723`.
The last foundation checkpoint remains `33c051e`, with runtime source `130e927`.
Do not mistake foundation evidence for dynamic-surface qualification.
Read the [profile contract](../../activity-groups-zg3b1.md) before applying these
results to another construct or provider.

| Gate | Result |
| --- | --- |
| Release tests | **1,441 passed**, zero failures/skips, 11 assembly/framework combinations on .NET 8/10 |
| Formatting | Full solution verify-no-changes passed on the committed source |
| Process loss | Four hard-exit modes on both frameworks; eight before/after receipts retained |
| Packages | All seven `0.2.0-preview.4` candidate packages built from the exact source commit |
| Package-only consumer | Passed both frameworks, including ordinary/protected execution, authorized groups, loops and native fan-out |
| Existing compiled consumer | Published `0.1.0-preview.15` consumer unchanged while candidate assemblies were substituted; both frameworks passed |
| Old worker | Rejected schema 9 on both frameworks; version and four retained step rows unchanged |
| Review | Independent Luna source review found no remaining concrete issue after the fresh-database marker-trigger fix |

The final SQLite suites were rerun after correcting a test-output fixture; the
nine other assembly/framework receipts remain valid because runtime sources were
unchanged. Schema-8 retained grouped-row migration is a separate test from the
published binary probe's schema-5→9 migration. Neither is Linux/remote evidence.

## Supported profile and evidence

- All four native functional/class keyed/positional fan-out overloads at root
  execution scope, using an explicit current completed selection step and the
  author's existing dependency edge. Native results and caller order remain.
- One definition yields distinct deterministic item occurrences with explicit
  parent identity. Keyed reorder reuses results; positional reorder fails the
  immutable selected-set contract. Simultaneous running siblings retain isolated
  memberships before either completes.
- Atomic whole-selection admission before item claims; bounded canonical selected
  hashes/identities, exact selection revision, monotonic capability marker.
- Exact item contracts, reserved-key collision rejection and owner/lease/generation
  fences before writes. Missing capability fails before selectors/item callbacks.
- Append-only closure for exact current item IDs/revisions. Selective item restart
  invalidates historical proof and appends a new proof, preserving occurrences and
  reusing siblings. An unchanged restarted selection binds a new expansion.
- Failed chunks remain open even if workflow code swallows failure; retries retain
  one occurrence/membership. Class compensation replays existing enumeration
  without new admission/closure or repeated forward callbacks.
- Mutable caller-owned input semantics match native steps: mutation before the
  next claim rejects against the admitted hash, with no callback for that item.
- Schema 9: genuine schema-8 upgrade retains grouped rows, authority binding,
  manifest/occurrence/membership identities; 5/6/7 upgrade regressions remain.
  Fresh/migrated markers cannot be cleared and retention cascades evidence.
- Original v1 manifest/root/loop golden vectors remain unchanged. Independent
  PowerShell SHA-256 vectors qualify v2 keyed and positional coordinates.

## Separate-process termination

Each mode exits the actual worker process with code **93**. A freshly opened
store is inspected before recovery callbacks. The committed JSON record retains
the run, canonical manifest, selection output, plan/expansion, exact selected
key/hash/occurrence tuples, occurrence IDs/coordinates/parents, step IDs/revisions/
attempts and exact memberships both before and after recovery.

| Mode | Termination boundary | Durable state before recovery | Recovery item callbacks |
| --- | --- | --- | --- |
| admit | Inside item-b insertion of whole admission transaction | Selection retained; enumeration, occurrences and marker rolled back | 2 |
| partial | Inside item-b membership claim after item a completes | Both selected items/all occurrences committed; only a's claim/result retained | 1 |
| close | Inside closure insertion after both item results commit | Full selected set and results, no current closure | 0 |
| closed | After closure commits, before run completion | Full selected set, results and current closure | 0 |

All modes preserve manifest/plan/parent identity, reuse every completed step and
finish with the native `A|B` result. This proves fixed-selection fan-out recovery;
it does not close the broader evolving-selection/fork work in ZG-3b.4.

## Reproduction

Run from the repository root with the installed .NET 8/10 SDKs:

```powershell
dotnet format Penghou.Zhinu.slnx --verify-no-changes --no-restore
dotnet test Penghou.Zhinu.slnx -c Release --no-restore --logger trx --results-directory artifacts/zg3b1-qualified-final-tests
dotnet pack Penghou.Zhinu.slnx -c Release --no-restore -o artifacts/zg3b1-packages
./eng/Verify-WorkflowPackageClosure.ps1 -ArtifactsDirectory artifacts/zg3b1-packages -PackageVersion 0.2.0-preview.4 -OutputDirectory artifacts/zg3b1-closure
./eng/Verify-LegacyWorkflowConsumer.ps1 -ArtifactsDirectory artifacts/zg3b1-packages -PackageVersion 0.2.0-preview.4 -OutputDirectory artifacts/zg3b1-legacy
```

Focused test suites: `ActivityGroupFanOutContractTests`,
`ActivityGroupFanOutAdmissionTests`, `ActivityGroupFanOutEngineTests`,
`ActivityGroupFanOutStoreTests`, `ActivityGroupFanOutRecoveryTests`.
The separate worker is in `Penghou.Zhinu.Sqlite.ProcessProbe/GroupFanOutProbe.cs`.
The final evidence record includes the actual source/hash and outcome receipts;
ignored local artifacts alone are not the handoff.

## Not yet qualified

Grouped forks; nested repeated fan-out/loop parent coordinates; changing selected
sets or inputs across boundary revisions; broader interrupted dynamic expansion;
parent child-invocation start/wait membership and boundary recovery; typed Fuwen
tokens or multi-activity fan-out lowering. ZG-3b.2 through 3b.6 remain open.

No ZG-4 hierarchy/query projection, ZG-5 aggregate state/progress, Fuwen adoption,
cross-repository completion, Linux/remote CI run or package publication is claimed.
Do not remove unsupported-shape rejection using this narrower checkpoint.
