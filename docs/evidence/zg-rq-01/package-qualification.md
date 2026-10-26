# Q4: package closure and local artifact qualification

Local qualification run at the bound candidate
`f0edf1714013106bdbce090179342ccb029eaf83`, Windows 11 x64, 2026-10-26
(2026-10-26T08:17 to 08:38 local, .NET 8.0.x / 10.0.x).

## Q4.1 clean build, matrix, pack, closure and consumer

| Step | Command | Result |
| --- | --- | --- |
| Restore | `dotnet restore Penghou.Zhinu.slnx` | PASS |
| Build | `dotnet build -c Release --no-restore` | PASS, 0 warnings, 0 errors |
| Format | `dotnet format --verify-no-changes --no-restore` | PASS, exit 0 |
| Test | `dotnet test -c Release --no-build --no-restore` (full solution) | 1,968 combined .NET 8/10 executions, 0 failed (984 per framework) |
| Pack | `dotnet pack -c Release --no-build -o artifacts` | 7 nupkg + 7 snupkg produced |
| Package closure | `Verify-WorkflowPackageClosure.ps1` | PASS: fresh cache, exact candidate closure, package-only restore, no project references |
| Legacy binary probe | `Verify-LegacyWorkflowConsumer.ps1` | PASS: precompiled published 0.1.0-preview.15 consumer runs unchanged, both frameworks |

Representative packaged execution beyond the closure probe: the Q1 recovery
process of this qualification itself restores the candidate packages
package-only (mapped local feed) and executes five real workflows through the
packaged runtime (see [compatibility.md](compatibility.md)).

## Q4.2 target framework and OS matrix

- Local (Windows): both .NET 8.0 and .NET 10.0, all six multi-target packages
  plus the ASP.NET Core package on .NET 10.
- Remote CI at the exact candidate commit: both jobs (ubuntu-latest,
  windows-latest) green — run
  [40190759599](https://github.com/jenolaszlo-sketch/penghou-zhinu/actions/runs/40190759599);
  the merge commit `83062a3` is additionally green
  ([40186032683](https://github.com/jenolaszlo-sketch/penghou-zhinu/actions/runs/40186032683)).
- macOS is not part of the Zhinu release policy matrix and is **not claimed**.

## Q4.3 archive consistency

Candidate nupkg SHA-256 (local build of `f0edf17`; these are candidate upload
bytes, distinct from the frozen bf33c05 Windows archive hashes and from any
future repository-signed public archives, as the release packet's provenance
policy requires):

```text
Penghou.Zhinu.0.2.0-preview.10.nupkg              b7b8d7eac1d8d9519700a887adf01079f94de1dbd91b4c9c2bc9ea37410059a3
Penghou.Zhinu.Agents.0.2.0-preview.10.nupkg      f7e079ab39278bed2f68d9e9036a1f5402de047214426fbf662939967ee05a05
Penghou.Zhinu.Hosting.0.2.0-preview.10.nupkg     c11250db536e860f4659c0ad3af5868c42714fab0147f013aa11ad6d51e8432e
Penghou.Zhinu.Hosting.AspNetCore.0.2.0-preview.10.nupkg 87fa753da8955a964f1a65af454dde71f8c284f9fa342868e5b533f32a5d8dd9
Penghou.Zhinu.OpenTelemetry.0.2.0-preview.10.nupkg  9a8f929d2081b52116581c53f314ddc107aebce6e95b19a4317b754cee51cf3c
Penghou.Zhinu.Sqlite.0.2.0-preview.10.nupkg       6e6c55e868ff3362458b1f8bfd8b6b8ae80d40b73930727a26612fee87e38f3b
Penghou.Zhinu.Testing.0.2.0-preview.10.nupkg     34f2e5dec48c559553bf5cc7057877e1ae453ed05d80648b0d122259335d3840
```

Verified per the closure qualification.json and direct nuspec inspection:
exact package IDs and version, expected assemblies per TFM, dependency ranges
exactly `Penghou.Workflow.Abstractions [0.1.0-preview.2]` and
`Microsoft.Extensions.Logging.Abstractions 10.0.9` for the core (Sqlite adds
the Microsoft.Data.Sqlite / SQLitePCLRaw pins), repository/license/readme
metadata present, no unintended local paths or development artifacts, internal
Zhinu dependencies at the matching release version.

## Q4.4 public NuGet qualification

**Not applicable at this stage.** All seven native preview.10 package URLs
return HTTP 404 at the recorded observation; publication is a separate
owner decision. Per the recorded release procedure, public archives must be
downloaded after indexing, hash-recorded separately (NuGet repository
signature changes the archive), signature-checked and qualified with fresh
caches before they may be selected as anyone's public dependency. No public
claim is made here.
