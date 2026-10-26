# Releasing and upgrading Zhinu

Zhinu is published on NuGet through `0.2.0-preview.4` (preview.3 â€”
execution-evidence reads and the operator CLI â€” from source `99d9d78`;
preview.4 â€” the durable authority-subject binding with SQLite schema 7 â€”
from source `0a523826`). The current source candidate is
**`0.2.0-preview.10`**: the merged activity-groups milestone (ZG-1 through
ZG-7 selected profiles) with SQLite schema 10, accepting additive upgrades
from 5, 6, 7, 8 and 9. Its exact local package/binary and process-loss
evidence is recorded in the [activity-group qualification
records](qualification/activity-groups/) and the release-preparation
packet referenced by the [roadmap](../ROADMAP.md); that evidence is not
publication evidence. A published version cannot be reused: the next
release must bump `Directory.Build.props`. Read the
[authorization boundary](workflow-authorization.md) before upgrading a host.

## Release set and ownership

Release these seven packages together at the version in `Directory.Build.props`:

- `Penghou.Zhinu`
- `Penghou.Zhinu.Sqlite`
- `Penghou.Zhinu.Hosting`
- `Penghou.Zhinu.Hosting.AspNetCore`
- `Penghou.Zhinu.Agents`
- `Penghou.Zhinu.Testing`
- `Penghou.Zhinu.OpenTelemetry`

Six packages target .NET 8 and .NET 10; ASP.NET Core targets .NET 10 only.
Internal Zhinu dependencies use the matching release version. The neutral
Workflow.Abstractions package is already published independently; neither it,
the IO packages, Luban nor Hufu needs republishing for this Zhinu release.
`Penghou.Hufu.Workflow` implementation and qualification follow the published
Zhinu runtime phase.

## Exact-source release handoff

Publication remains unauthorized during ZG-RQ-02. RG-02 is OWNER DECISION REQUIRED;
the owner explicitly requested no live protection-policy changes. Current owner
self-review is a manual checkpoint, not independent approval.

CI builds, tests, packs and probes the exact commit on Windows and Ubuntu. Its
Ubuntu producer uploads seven package/symbol pairs plus a hash/source/run manifest.
The publisher requires the latest qualifying CI run of the exact requested SHA
to succeed, including both platform jobs, and verifies the artifact's run, attempt,
source, repository, manifest, ZIP identities and hashes. It never rebuilds.

The accepted manual `main` dispatch path remains available only for a qualified
exact SHA and its checked-in version. A tag path must identify that source and
equal `v<checked-in-version>`; tags cannot override the qualified package version.
Ordinary branch pushes cannot invoke the publisher. After verification, the `nuget`
environment gates the final job, which rechecks every archive before requesting
OIDC credentials. Environment/API configuration alone does not prove independent
authorization or NuGet's external trusted-publisher restrictions.

For a future separately authorized release, retain CI run/attempt/artifact IDs,
source SHA and manifest; obtain the required owner-policy decision and approval;
then verify public package indexing and fresh consumer restores. Do not reuse a
published version: `--skip-duplicate` cannot replace NuGet contents.

The credential-free release rehearsal reuses the production verifier and ends at
a real protected environment checkpoint. Offline fault fixtures are explicitly
separate from hosted enforcement evidence.

## SQLite migration

Stop older workers and back up the database before opening it with the new
SQLite package, including through the CLI or an inspection host. Initialization
transactionally upgrades version 5 with additive nullable columns and new
authorization tables; schema 7 (preview.4) adds the run authority-subject
binding through the same additive pattern. Existing rows remain unprotected.
Older schemas are rejected as before.

Schema-5 binaries reject the schema-6 database. Do not run mixed worker versions
or lower the schema marker. Downgrading requires restoring the backup. New
authorization hooks require a protected provider profile on newly admitted runs;
opening the database does not retrofit that profile onto old runs.

## Candidate evidence

Implementation commit `e91804a` passed local Release builds and 1,017 Windows
test cases across .NET 8/10, with package closure, unchanged preview.15 binary
compatibility, retained-row migration and old-worker rejection checks. See
[local qualification](qualification/workflow-authorization.md).

The first remote run, [37132951810](https://github.com/jenolaszlo-sketch/penghou-zhinu/actions/runs/37132951810),
passed Ubuntu build, formatting, tests and pack, then found inherited library
analyzer settings in the generated consumer. The consumer now supplies its own
empty `Directory.Build.props` and `Directory.Build.targets`; the nested consumer
passes locally on both frameworks. Windows CI also exposed a signal deadline
test that depended on 300 milliseconds of wall-clock time. It now uses a
controllable clock, and all four signal-parking tests pass on both frameworks.
These corrections affect qualification tooling and tests only. A successful run
for the corrected release commit and user-run NuGet publication remain the
release gates; the earlier local record is not a remote CI or publication claim.

The follow-up [run 37135986361](https://github.com/jenolaszlo-sketch/penghou-zhinu/actions/runs/37135986361)
passed the complete Windows job. Ubuntu passed build, format, tests, pack and
package closure, then exposed mixed-case archive filenames in the legacy
probe's NuGet cache lookup. Both lookup filenames now use the lowercase package
ID, matching the restored cache on case-sensitive filesystems. The corrected
legacy probe passes locally on .NET 8/10 with a fresh cache, unchanged consumer
binaries, schema-5-to-6 migration and old-worker rejection. Restored old DLLs
are also checked against their original hashes. An explicit filename-case check
rejects both prior paths and accepts both corrected paths. These checks are
local Windows evidence; the new Ubuntu CI run remains the Linux qualification.
