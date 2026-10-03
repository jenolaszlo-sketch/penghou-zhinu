# Releasing and upgrading Zhinu

The current source candidate is `0.2.0-preview.1`. It adds optional execution
authorization against exact `Penghou.Workflow.Abstractions` `0.1.0-preview.2`
and upgrades SQLite from schema 5 to 6. Read the [release notes](releases/0.2.0-preview.1.md)
and [authorization boundary](workflow-authorization.md) before upgrading a host.

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

## Publish from main

1. Commit and push the release source and documentation to `main`.
2. Confirm [CI](https://github.com/jenolaszlo-sketch/penghou-zhinu/actions/workflows/ci.yml)
   passes for that exact commit on Windows and Ubuntu. It builds, verifies
   formatting, tests declared frameworks, packs all seven packages, and runs
   isolated package and legacy-binary probes. Qualification artifacts retain
   TRX results, packages and consumer logs.
3. In [Publish to NuGet](https://github.com/jenolaszlo-sketch/penghou-zhinu/actions/workflows/publish.yml),
   choose **Run workflow** on **main**. There are no version inputs. The existing
   NuGet OIDC identity publishes the package and symbol set using the checked-in
   version. The publish workflow packs independently; it does not wait for or
   rerun ordinary CI, so check that CI has passed before dispatching it.
4. Check all seven package pages and restore a fresh package-only consumer from
   nuget.org. Record the publication run, exact versions and public artifact
   evidence before closing ZA-6 or starting Hufu integration.

The workflow also supports `v*` tags, where the tag overrides the package version.
The normal release path here is the input-free `main` run. Ordinary branch
pushes and CI runs do not publish packages. Do not reuse a published version:
`--skip-duplicate` cannot update existing NuGet contents.

## SQLite migration

Stop older workers and back up the database before opening it with the new
SQLite package, including through the CLI or an inspection host. Initialization
transactionally upgrades version 5 with additive nullable columns and new
authorization tables. Existing rows remain unprotected. Older schemas are
rejected as before.

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
